using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace V380Decoder.src
{
    public class V380Client : IDisposable
    {
        public SnapshotManager snapshotManager { get; private set; }
        private TcpClient authClient, streamClient;
        private NetworkStream authStream, streamStream;
        private readonly string ip;
        private readonly int port;
        private readonly uint deviceId;
        private readonly string username, password;
        private readonly SourceStream source;
        private readonly OutputMode mode;
        private readonly int streamQuality;
        private uint authTicket, sessionId;
        private ushort deviceVersion, communicationVersion;
        private int frameWidth = 1280;
        private int frameheight = 720;
        private byte[] aesKey = new byte[16];
        private bool needReconnect = false;

        // ---- Video layout auto-detection -------------------------------------------------
        // Different V380 models put the media payload at different offsets inside the
        // reassembled frame, and the AES grid is anchored on the payload start. A misaligned
        // grid can still yield a valid-looking first NAL (16-byte aligned shift), so the layout
        // is only trusted once a parameter set (SPS) parses as a coherent H.264/H.265 SPS.
        // The validated layout (offset, decrypt, codec) is then locked for the session.
        private static readonly int[] VideoOffsetCandidates = { 0, 4, 8, 12, 16, 20 };
        private const int LayoutMissesBeforeRelock = 8;
        private int lockedVideoOffset = -1;
        private bool lockedVideoDecrypt;
        private VideoCodec lockedVideoCodec = VideoCodec.Unknown;
        private int lockedLayoutMisses;
        private readonly HashSet<byte> lockedVideoRawTypes = new();

        public V380Client(string ip, int port, uint deviceId, string username, string password, SourceStream source, OutputMode mode, int streamQuality)
        {
            this.ip = ip;
            this.port = port;
            this.deviceId = deviceId;
            this.username = username;
            this.password = password;
            this.source = source;
            this.mode = mode;
            this.streamQuality = streamQuality;
            if (mode == OutputMode.Rtsp)
                snapshotManager = new SnapshotManager();
        }

        public void Run(RtspServer rtsp, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    int auth = GetAuthTicket();
                    if (auth == 0) continue;
                    if (auth == -1) break;

                    if (!StreamLogin())
                    {
                        Console.Error.WriteLine("[STREAM] Retrying...");
                        streamStream?.Close(); streamClient?.Close();
                        continue;
                    }

                    if (!StartStream())
                    {
                        Console.Error.WriteLine("[STREAM] Retrying...");
                        continue;
                    }

                    ReceiveFrames(mode, rtsp, ct);
                }
                catch (OperationCanceledException)
                {
                    Console.Error.WriteLine("[STREAM] Operation cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ERROR] Fatal error: {ex.Message}");
                    Console.Error.WriteLine($"[ERROR] Stack trace: {ex.StackTrace}");
                    Thread.Sleep(3000);
                }
                finally
                {
                    Console.Error.WriteLine("[STREAM] Closing connection...");
                    streamStream?.Close(); streamClient?.Close();
                }
            }
        }

        public int GetAuthTicket()
        {
            try
            {
                authClient = new TcpClient();
                var r = authClient.BeginConnect(ip, port, null, null);
                if (!r.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
                {
                    Console.Error.WriteLine("[AUTH] failed connecting to socket. retrying...");
                    return 0;
                }
                authClient.EndConnect(r);
                authStream = authClient.GetStream();

                var encryptedPassword = GeneratePassword(password);
                var userBytes = Encoding.ASCII.GetBytes(username);
                var cmd1167 = new byte[520];
                WriteUInt32LE(cmd1167, 0, 1167); // command
                if (source == SourceStream.Lan)
                {
                    WriteUInt32LE(cmd1167, 4, 120); // unknown1
                    cmd1167[8] = 31; // unknown2 (version?)
                    WriteUInt32LE(cmd1167, 9, 1); // unknown3            
                    WriteUInt32LE(cmd1167, 13, deviceId); // deviceId
                    Array.Copy(userBytes, 0, cmd1167, 49, Math.Min(userBytes.Length, 32)); //username
                    Array.Copy(encryptedPassword, 0, cmd1167, 81, Math.Min(encryptedPassword.Length, 64)); //password
                }
                else
                {
                    WriteUInt32LE(cmd1167, 4, 1022); // unknown1
                    cmd1167[8] = 31; // unknown2 (version?)
                    WriteUInt32LE(cmd1167, 9, 1); // unknown3 
                    WriteUInt32LE(cmd1167, 13, deviceId); // deviceId
                    var hostnameBytes = Encoding.ASCII.GetBytes($"{deviceId}.nvdvr.net");
                    Array.Copy(hostnameBytes, 0, cmd1167, 17, Math.Min(hostnameBytes.Length, 50)); //hostname
                    WriteUInt32LE(cmd1167, 67, (uint)port); //port
                    Array.Copy(userBytes, 0, cmd1167, 71, Math.Min(userBytes.Length, 32)); //username
                    Array.Copy(encryptedPassword, 0, cmd1167, 103, Math.Min(encryptedPassword.Length, 64)); //password
                }
                if (!SendData(authStream, cmd1167))
                {
                    Console.Error.WriteLine("[AUTH] failed send request. retrying...");
                    return 0;
                }

                var resp = ReceiveData(authStream, 256);
                if (resp == null || resp.Length < 256)
                {
                    Console.Error.WriteLine("[AUTH] failed receive response. retrying...");
                    return 0;
                }

                uint respCmd = ReadUInt32LE(resp, 0);
                if (respCmd != 1168)
                {
                    Console.Error.WriteLine($"[AUTH] invalid response cmd: {respCmd} (expected 1168). retrying...");
                    return 0;
                }
                uint loginResult = ReadUInt32LE(resp, 4);
                if (loginResult != 1001)
                {
                    if (loginResult == 1011)
                        Console.Error.WriteLine($"[AUTH] invalid username. exiting...");
                    else if (loginResult == 1012)
                        Console.Error.WriteLine($"[AUTH] invalid password. exiting...");
                    else if (loginResult == 1018)
                        Console.Error.WriteLine($"[AUTH] invalid device id. exiting...");
                    else
                        Console.Error.WriteLine($"[AUTH] login failed result: {loginResult} (expected 1001). exiting...");

                    return -1;
                }

                uint resultValue = ReadUInt32LE(resp, 8);
                byte version = resp[12];
                uint ticket = ReadUInt32LE(resp, 13);
                uint session = ReadUInt32LE(resp, 17);
                byte deviceType = resp[21];
                byte camType = resp[22];
                uint vendorId = ReadUInt16LE(resp, 23);
                uint isDomainExists = resp[25];
                byte[] domainBytes = new byte[32];
                Array.Copy(resp, 26, domainBytes, 0, 32);
                string domain = Encoding.ASCII.GetString(domainBytes).TrimEnd('\0');

                LogUtils.debug($"[AUTH] response success");
                LogUtils.debug($"[AUTH] cmd: {respCmd}");
                LogUtils.debug($"[AUTH] result: {loginResult}");
                LogUtils.debug($"[AUTH] authTicket: {ticket} (0x{ticket:X})");
                LogUtils.debug($"[AUTH] resultValue: {resultValue}");
                LogUtils.debug($"[AUTH] version: {version}");
                LogUtils.debug($"[AUTH] session: {session}");
                LogUtils.debug($"[AUTH] deviceType: {deviceType}");
                LogUtils.debug($"[AUTH] camType: {camType}");
                LogUtils.debug($"[AUTH] vendorId: {vendorId}");
                LogUtils.debug($"[AUTH] isDomainExists: {isDomainExists}");
                LogUtils.debug($"[AUTH] domain: {domain}");

                deviceVersion = version;
                sessionId = session;
                authTicket = ticket;

                Console.Error.WriteLine($"[AUTH] success ticket={authTicket} deviceVersion={deviceVersion}");
                authStream.Close(); authClient.Close();
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AUTH] Error: {ex.Message}");
                return 0;
            }
            finally
            {
                authStream?.Close(); authClient?.Close();
            }
        }


        public bool StreamLogin()
        {
            streamClient = new TcpClient
            {
                NoDelay = true,
                ReceiveBufferSize = 0x8000
            };
            var r = streamClient.BeginConnect(ip, port, null, null);
            if (!r.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
            {
                Console.Error.WriteLine("[STREAM] failed connecting to socket. retrying...");
                return false;
            }
            ;
            streamClient.EndConnect(r);
            streamStream = streamClient.GetStream();

            var cmd301 = new byte[256];
            WriteUInt32LE(cmd301, 0, 301);
            if (source == SourceStream.Lan)
            {
                WriteUInt32LE(cmd301, 4, deviceId); //device id
                WriteUInt32LE(cmd301, 8, 0); //unknown1 
                WriteUInt16LE(cmd301, 12, 20); //unknown2 
                WriteUInt32LE(cmd301, 14, authTicket); //auth ticket
                WriteUInt32LE(cmd301, 22, 4097); //audio 4096=off, 4097=on
                WriteUInt32LE(cmd301, 26, (uint)streamQuality);    //quality  0=SD, 1=HD
            }
            else
            {
                WriteUInt32LE(cmd301, 4, 1022); //unknown1 
                var hostnameBytes = Encoding.ASCII.GetBytes($"{deviceId}.nvdvr.net");
                Array.Copy(hostnameBytes, 0, cmd301, 8, Math.Min(hostnameBytes.Length, 50)); //hostname
                WriteUInt32LE(cmd301, 58, (uint)port); //port
                WriteUInt32LE(cmd301, 62, deviceId); //device id
                WriteUInt32LE(cmd301, 66, authTicket); // auth ticket
                WriteUInt32LE(cmd301, 70, sessionId); // session id
                WriteUInt32LE(cmd301, 74, (uint)streamQuality); //quality 0=SD, 1=HD
                cmd301[78] = 20; //unknown2 
                WriteUInt32LE(cmd301, 79, 1); //unknown23
            }

            if (!SendData(streamStream, cmd301))
            {
                Console.Error.WriteLine("[STREAM] login failed send request");
                return false;
            }

            var resp401 = ReceiveData(streamStream, 412);

            if (resp401 == null || resp401.Length < 8)
            {
                Console.Error.WriteLine("[STREAM] login failed receive response");
                return false;
            }
            var respCmd = ReadUInt32LE(resp401, 0);
            if (respCmd != 401)
            {
                Console.Error.WriteLine($"[STREAM] login invalid response cmd: {respCmd} (expected 401)");
                return false;
            }

            int result = (int)ReadUInt32LE(resp401, 4);
            if (result == -11 || result == -12)
            {
                Console.Error.WriteLine($"[STREAM] login failed result={result}");
                return false;
            }

            LogUtils.debug($"[STREAM] login response success");
            LogUtils.debug($"[STREAM] login cmd: {respCmd}");
            if (source == SourceStream.Lan)
            {
                ushort version = ReadUInt16LE(resp401, 8);
                uint width = ReadUInt32LE(resp401, 10);
                uint height = ReadUInt32LE(resp401, 14);
                uint maxPackSize = ReadUInt32LE(resp401, 18);
                byte audioFreq = resp401[22];
                byte audioBits = resp401[23];
                byte audioChannels = resp401[24];
                communicationVersion = version;
                frameWidth = (int)width;
                frameheight = (int)height;
                LogUtils.debug($"[STREAM] login result: {result}");
                LogUtils.debug($"[STREAM] login version: {version}");
                LogUtils.debug($"[STREAM] login width: {width}");
                LogUtils.debug($"[STREAM] login height: {height}");
                LogUtils.debug($"[STREAM] login maxPackSize: {maxPackSize}");
                LogUtils.debug($"[STREAM] login audioFreq: {audioFreq}");
                LogUtils.debug($"[STREAM] login audioBits: {audioBits}");
                LogUtils.debug($"[STREAM] login audioChannels: {audioChannels}");
            }

            if (deviceVersion > 30) GenerateMediaKey(authTicket);
            Console.Error.WriteLine($"[STREAM] login OK quality={(streamQuality == 0 ? "SD" : "HD")}");
            return true;
        }

        public bool StartStream()
        {
            Console.Error.WriteLine($"[STREAM] starting stream...");
            var cmd303 = new byte[256];
            WriteUInt32LE(cmd303, 0, 303);
            WriteUInt16LE(cmd303, 4, 0x3001);
            return SendData(streamStream, cmd303);
        }

        public void ReceiveFrames(OutputMode mode, RtspServer rtsp, CancellationToken ct)
        {
            bool needDecrypt = deviceVersion > 30;
            ResetVideoLayout("stream start");

            var frameFrags = new List<byte>();
            ushort frameTotal = 0;
            ushort nextFragment = 0;
            byte frameStartType = 0;
            bool assemblingFrame = false;

            var header12 = new byte[12];
            var payloadBuf = new byte[65536];

            // stdout is used only for Video or Audio output modes
            Stream stdout = (mode == OutputMode.Video || mode == OutputMode.Audio)
                ? Console.OpenStandardOutput()
                : null;

            Console.Error.WriteLine($"[RECV] mode={mode} decrypt={needDecrypt} communicationVersion={communicationVersion}");

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (needReconnect)
                    {
                        Console.Error.WriteLine($"[STREAM] lost, reconnecting... ");
                        try { streamStream?.Close(); streamClient?.Close(); } catch { }
                        if (!StreamLogin()) break;
                        if (!StartStream()) break;
                        needReconnect = false;
                        ResetVideoLayout("reconnect");
                    }

                    // 12-byte fragment header
                    if (ReadExact(streamStream, header12, 0, 12) < 12) continue;

                    if (header12[0] != 0x7F)
                    {
                        continue;
                    }

                    byte type = header12[1];
                    ushort totalFrame = ReadUInt16LE(header12, 3);
                    ushort curFrame = ReadUInt16LE(header12, 5);
                    ushort payLen = ReadUInt16LE(header12, 7);

                    if (payLen == 0 || payLen > 20000 || totalFrame == 0 || curFrame >= totalFrame)
                    {
                        Console.Error.WriteLine($"[SKIP] invalid header type=0x{type:X2} total={totalFrame} cur={curFrame} len={payLen}");
                        continue;
                    }

                    if (payloadBuf.Length < payLen) payloadBuf = new byte[payLen];
                    if (ReadExact(streamStream, payloadBuf, 0, payLen) < payLen) continue;

                    if (type == 0x5B)
                    {
                        continue;
                    }

                    if (curFrame == 0 || !assemblingFrame || totalFrame != frameTotal || curFrame != nextFragment)
                    {
                        if (assemblingFrame && curFrame != 0)
                        {
                            LogUtils.debug($"[FRAME] resync from type=0x{frameStartType:X2} expected={nextFragment} got={curFrame} total={totalFrame}");
                        }

                        frameFrags.Clear();
                        frameTotal = totalFrame;
                        nextFragment = 0;
                        frameStartType = type;
                        assemblingFrame = true;
                    }

                    for (int i = 0; i < payLen; i++) frameFrags.Add(payloadBuf[i]);
                    nextFragment = (ushort)(curFrame + 1);

                    if (curFrame != totalFrame - 1) continue;
                    if (frameFrags.Count < 16)
                    {
                        frameFrags.Clear();
                        assemblingFrame = false;
                        continue;
                    }

                    byte[] full = frameFrags.ToArray();
                    frameFrags.Clear();
                    assemblingFrame = false;

                    if (!HandleAsMediaFrame(frameStartType, full, needDecrypt, mode, rtsp, stdout))
                    {
                        Console.Error.WriteLine($"[FRAME] unknown type=0x{frameStartType:X2} len={full.Length}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("[RECV] Operation cancelled");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RECV] {ex.Message}");
                Console.Error.WriteLine($"[RECV] {ex.StackTrace}");
            }
        }

        private bool HandleAsMediaFrame(byte rawType, byte[] full, bool needDecrypt, OutputMode mode, RtspServer rtsp, Stream stdout)
        {
            ushort outerFrameType = full.Length >= 6 ? ReadUInt16LE(full, 4) : (ushort)0;
            uint outerFrameId = full.Length >= 4 ? ReadUInt32LE(full, 0) : 0;
            ushort outerFrameRate = full.Length >= 8 ? ReadUInt16LE(full, 6) : (ushort)0;
            ulong outerTimestamp = full.Length >= 16 ? ReadUInt64LE(full, 8) : 0;

            if (rawType == 0x1A)
            {
                if (full.Length < 16) return false;

                uint frameId = ReadUInt32LE(full, 0);
                ushort frameType = ReadUInt16LE(full, 4);
                ushort frameRate = ReadUInt16LE(full, 6);
                ulong timestamp = ReadUInt64LE(full, 8);

                byte[] payload = new byte[full.Length - 16];
                Array.Copy(full, 16, payload, 0, payload.Length);

                if (needDecrypt)
                {
                    if (communicationVersion == 21)
                        DecryptMediaPre2k(payload, payload.Length, 1);
                    else
                        DecryptAudioFrame(payload, payload.Length);
                }

                var audioFrame = new FrameData
                {
                    RawType = rawType,
                    FrameId = frameId,
                    FrameType = frameType,
                    FrameRate = frameRate,
                    Timestamp = timestamp,
                    Payload = payload
                };

                if (mode == OutputMode.Audio)
                {
                    stdout.Write(payload, 0, payload.Length);
                    stdout.Flush();
                }
                else if (mode == OutputMode.Rtsp)
                {
                    rtsp?.PushAudio(audioFrame);
                }

                return true;
            }

            if (rawType == 0x16)
            {
                byte[] audioPayload = ExtractAudioPayload(full, needDecrypt);
                if (audioPayload.Length == 0) return false;

                if (mode == OutputMode.Audio)
                {
                    stdout.Write(audioPayload, 0, audioPayload.Length);
                    stdout.Flush();
                }
                else if (mode == OutputMode.Rtsp)
                {
                    rtsp?.PushAudio(new FrameData
                    {
                        RawType = rawType,
                        FrameId = 0,
                        FrameType = 0,
                        FrameRate = 0,
                        Timestamp = 0,
                        Payload = audioPayload
                    });
                }

                return true;
            }

            if (!TryExtractVideoPayload(rawType, full, needDecrypt, out var normalizedPayload, out var videoCodec))
            {
                LogUtils.debug($"[FRAME] unclassified type=0x{rawType:X2} len={full.Length} head={BitConverter.ToString(full, 0, Math.Min(16, full.Length))}");
                return false;
            }

            bool isKeyFrame = IsKeyVideoFrame(rawType, outerFrameType, normalizedPayload, videoCodec);

            if (mode == OutputMode.Rtsp)
            {
                snapshotManager.UpdateFrame(normalizedPayload, frameWidth, frameheight, isKeyFrame, outerTimestamp);
            }

            var videoFrame = new FrameData
            {
                RawType = rawType,
                FrameId = outerFrameId,
                FrameType = outerFrameType,
                FrameRate = outerFrameRate,
                Timestamp = outerTimestamp,
                Codec = videoCodec,
                Payload = normalizedPayload
            };

            if (mode == OutputMode.Video)
            {
                stdout.Write(normalizedPayload, 0, normalizedPayload.Length);
                stdout.Flush();
            }
            else if (mode == OutputMode.Rtsp)
            {
                rtsp?.PushVideo(videoFrame);
            }

            return true;
        }

        private byte[] ExtractAudioPayload(byte[] full, bool needDecrypt)
        {
            byte[] payload;

            if (full.Length > 20)
            {
                payload = new byte[full.Length - 20];
                Array.Copy(full, 20, payload, 0, payload.Length);
            }
            else
            {
                payload = (byte[])full.Clone();
            }

            if (needDecrypt && payload.Length >= 16)
            {
                if (communicationVersion == 21)
                    DecryptMediaPre2k(payload, payload.Length, 1);
                else
                    DecryptAudioFrame(payload, payload.Length);
            }

            return payload;
        }

        // =====================================================================================
        // Video payload extraction with layout auto-detection
        // =====================================================================================

        private enum SpsState { Invalid = 0, Absent = 1, Valid = 2 }

        private sealed class VideoCandidate
        {
            public int Offset;
            public bool Decrypted;
            public byte[] Payload;
            public int Leading;          // bytes skipped before the first start code
            public int Score;            // legacy NAL heuristic score
            public SpsState Sps;
            public VideoCodec SpsCodec = VideoCodec.Unknown;
            public int SpsWidth, SpsHeight;
            public int Order;            // enumeration order, last tie-breaker
        }

        private void ResetVideoLayout(string reason)
        {
            if (lockedVideoOffset >= 0)
                Console.Error.WriteLine($"[VIDEO] layout unlocked ({reason})");
            lockedVideoOffset = -1;
            lockedVideoDecrypt = false;
            lockedVideoCodec = VideoCodec.Unknown;
            lockedLayoutMisses = 0;
            lockedVideoRawTypes.Clear();
        }

        private bool TryExtractVideoPayload(byte rawType, byte[] full, bool needDecrypt, out byte[] normalizedPayload, out VideoCodec codec)
        {
            // 1) Fast path: layout already validated on this session.
            if (lockedVideoOffset >= 0)
            {
                if (full.Length > lockedVideoOffset)
                {
                    byte[] p = BuildVideoCandidate(full, lockedVideoOffset, lockedVideoDecrypt);
                    if (TryNormalizeVideoPayload(p, out var cand, out _) &&
                        IsPlausibleNalHeader(cand, NalHeaderIndex(cand), lockedVideoCodec))
                    {
                        lockedLayoutMisses = 0;
                        lockedVideoRawTypes.Add(rawType);
                        normalizedPayload = cand;
                        codec = lockedVideoCodec;
                        return true;
                    }
                }

                // Frame types that never carried video under this layout (metadata such as 0x18)
                // are simply not video: they must not trigger a re-detection.
                if (lockedVideoRawTypes.Contains(rawType) && ++lockedLayoutMisses >= LayoutMissesBeforeRelock)
                {
                    Console.Error.WriteLine($"[VIDEO] {lockedLayoutMisses} consecutive frames do not match the locked layout");
                    ResetVideoLayout("layout mismatch");
                }

                normalizedPayload = Array.Empty<byte>();
                codec = VideoCodec.Unknown;
                return false;
            }

            // 2) Detection: evaluate every (offset, decrypt) hypothesis.
            VideoCandidate best = null;
            int order = 0;
            foreach (int offset in VideoOffsetCandidates)
            {
                if (full.Length <= offset) continue;

                var modes = (needDecrypt && full.Length - offset >= 16) ? new[] { true, false } : new[] { false };
                foreach (bool decrypt in modes)
                {
                    byte[] payload = BuildVideoCandidate(full, offset, decrypt);
                    if (!TryNormalizeVideoPayload(payload, out var normalized, out int leading))
                        continue;

                    int score = ScoreVideoPayload(normalized);
                    if (score == int.MinValue) continue;

                    var c = new VideoCandidate
                    {
                        Offset = offset, Decrypted = decrypt, Payload = normalized,
                        Leading = leading, Score = score, Order = order++
                    };
                    AnalyzeParameterSets(c);

                    if (best == null || IsBetterCandidate(c, best)) best = c;
                }
            }

            if (best == null)
            {
                normalizedPayload = Array.Empty<byte>();
                codec = VideoCodec.Unknown;
                return false;
            }

            if (best.Sps == SpsState.Valid)
            {
                lockedVideoOffset = best.Offset;
                lockedVideoDecrypt = best.Decrypted;
                lockedVideoCodec = best.SpsCodec;
                lockedLayoutMisses = 0;
                lockedVideoRawTypes.Clear();
                lockedVideoRawTypes.Add(rawType);
                Console.Error.WriteLine($"[VIDEO] layout locked: offset={best.Offset} decrypt={best.Decrypted} codec={best.SpsCodec} " +
                                        $"sps={best.SpsWidth}x{best.SpsHeight} leading={best.Leading} (device reported {frameWidth}x{frameheight})");
                if (best.SpsWidth != frameWidth || best.SpsHeight != frameheight)
                    Console.Error.WriteLine($"[VIDEO] note: SPS size differs from the size reported at login");
            }

            normalizedPayload = best.Payload;
            codec = best.Sps == SpsState.Valid ? best.SpsCodec : DetectVideoCodec(best.Payload);
            return true;
        }

        // Ranking: a coherent SPS beats everything, then a payload starting exactly on a start
        // code (the AES grid is anchored on the payload start), then the legacy NAL score.
        private static bool IsBetterCandidate(VideoCandidate c, VideoCandidate best)
        {
            if (c.Sps != best.Sps) return c.Sps > best.Sps;
            if (c.Leading != best.Leading) return c.Leading < best.Leading;
            if (c.Score != best.Score) return c.Score > best.Score;
            return c.Order < best.Order;
        }

        private byte[] BuildVideoCandidate(byte[] full, int offset, bool decrypt)
        {
            byte[] data = new byte[full.Length - offset];
            Array.Copy(full, offset, data, 0, data.Length);
            if (decrypt && data.Length >= 16)
            {
                if (communicationVersion == 21)
                    DecryptMediaPre2k(data, data.Length, 1);
                else
                    DecryptVideoFrame(data, data.Length);
            }
            return data;
        }

        private bool TryNormalizeVideoPayload(byte[] payload, out byte[] normalizedPayload, out int leading)
        {
            int start = FindVideoStartCode(payload);
            leading = start;
            if (start < 0)
            {
                normalizedPayload = payload;
                return false;
            }

            if (payload[start] == 0 && payload[start + 1] == 0 && payload[start + 2] == 1)
            {
                normalizedPayload = new byte[payload.Length - start + 1];
                normalizedPayload[0] = 0;
                Array.Copy(payload, start, normalizedPayload, 1, payload.Length - start);
                return true;
            }

            normalizedPayload = new byte[payload.Length - start];
            Array.Copy(payload, start, normalizedPayload, 0, normalizedPayload.Length);
            return true;
        }

        private static int NalHeaderIndex(byte[] payload)
            => payload.Length >= 4 && payload[2] == 0 && payload[3] == 1 ? 4 : 3;

        // Header check for a known codec (stricter than IsLikelyNalHeader).
        private static bool IsPlausibleNalHeader(byte[] p, int i, VideoCodec codec)
        {
            if (i >= p.Length) return false;
            if ((p[i] & 0x80) != 0) return false;                 // forbidden_zero_bit
            if (codec == VideoCodec.H265)
            {
                if (i + 1 >= p.Length) return false;
                int type = (p[i] >> 1) & 0x3F;
                int layerId = ((p[i] & 1) << 5) | (p[i + 1] >> 3);
                int tid = p[i + 1] & 7;
                return type <= 40 && layerId == 0 && tid >= 1;
            }
            if (codec == VideoCodec.H264)
            {
                int type = p[i] & 0x1F;
                return type is >= 1 and <= 23;
            }
            return false;
        }

        private void AnalyzeParameterSets(VideoCandidate c)
        {
            c.Sps = SpsState.Absent;
            byte[] p = c.Payload;
            int i = 0, nals = 0;
            while (i < p.Length - 4 && nals < 16)
            {
                int sc = FindNextStartCode(p, i);
                if (sc < 0) break;
                int nalStart = sc + (p[sc + 2] == 1 ? 3 : 4);
                if (nalStart >= p.Length) break;
                int next = FindNextStartCode(p, nalStart);
                int nalEnd = next < 0 ? p.Length : next;
                nals++;

                byte h = p[nalStart];
                bool h265Sps = (h & 0x81) == 0 && ((h >> 1) & 0x3F) == 33;
                bool h264Sps = (h & 0x80) == 0 && (h & 0x1F) == 7;
                if (h265Sps || h264Sps)
                {
                    int len = nalEnd - nalStart;
                    if (h265Sps && TryParseH265Sps(p, nalStart, len, out int w, out int hgt))
                    {
                        c.Sps = SpsState.Valid; c.SpsCodec = VideoCodec.H265; c.SpsWidth = w; c.SpsHeight = hgt;
                        return;
                    }
                    if (h264Sps && TryParseH264Sps(p, nalStart, len, out w, out hgt))
                    {
                        c.Sps = SpsState.Valid; c.SpsCodec = VideoCodec.H264; c.SpsWidth = w; c.SpsHeight = hgt;
                        return;
                    }
                    c.Sps = SpsState.Invalid;   // an SPS is there but is not coherent: wrong layout
                }
                i = nalEnd;
            }
        }

        private bool IsKeyVideoFrame(byte rawType, ushort frameType, byte[] payload, VideoCodec codec)
        {
            if (rawType == 0x00) return true;

            int nalOffset = NalHeaderIndex(payload);
            if (payload.Length <= nalOffset) return false;
            byte nalHeader = payload[nalOffset];

            if (codec == VideoCodec.H265)
                return ((nalHeader >> 1) & 0x3F) is >= 16 and <= 21 or 32 or 33 or 34 || frameType == 0;
            if (codec == VideoCodec.H264)
                return (nalHeader & 0x1F) is 5 or 7 or 8 || frameType == 0;

            int h264NalType = nalHeader & 0x1F;
            if (h264NalType == 5 || h264NalType == 7 || h264NalType == 8) return true;
            int h265NalType = (nalHeader >> 1) & 0x3F;
            if (h265NalType is 19 or 20 or 32 or 33 or 34) return true;
            return frameType == 0;
        }

        private int FindVideoStartCode(byte[] payload)
        {
            int limit = Math.Min(payload.Length - 4, 128);
            for (int i = 0; i <= limit; i++)
            {
                if (payload[i] == 0 && payload[i + 1] == 0 && payload[i + 2] == 0 && payload[i + 3] == 1)
                {
                    if (IsLikelyNalHeader(payload, i + 4)) return i;
                }

                if (payload[i] == 0 && payload[i + 1] == 0 && payload[i + 2] == 1)
                {
                    if (IsLikelyNalHeader(payload, i + 3)) return i;
                }
            }

            return -1;
        }

        private int ScoreVideoPayload(byte[] payload)
        {
            int score = 0;
            int nalCount = 0;
            int parameterSetCount = 0;
            int idrCount = 0;

            int i = 0;
            while (i < payload.Length - 4 && nalCount < 12)
            {
                int sc = FindNextStartCode(payload, i);
                if (sc < 0) break;

                int scLen = payload[sc + 2] == 1 ? 3 : 4;
                int nalStart = sc + scLen;
                if (!IsLikelyNalHeader(payload, nalStart))
                {
                    i = sc + 1;
                    score -= 20;
                    continue;
                }

                nalCount++;
                byte nalHeader = payload[nalStart];
                int h264NalType = nalHeader & 0x1F;
                int h265NalType = (nalHeader >> 1) & 0x3F;

                if (h264NalType is 7 or 8 || h265NalType is 32 or 33 or 34)
                {
                    parameterSetCount++;
                    score += 50;
                }

                if (h264NalType == 5 || h265NalType is 19 or 20)
                {
                    idrCount++;
                    score += 30;
                }

                score += 10;
                i = nalStart + 1;
            }

            if (nalCount == 0) return int.MinValue;

            score += nalCount * 5;
            score += parameterSetCount * 40;
            score += idrCount * 20;

            return score;
        }

        private int FindNextStartCode(byte[] payload, int from)
        {
            for (int i = from; i + 3 < payload.Length; i++)
            {
                if (payload[i] == 0 && payload[i + 1] == 0)
                {
                    if (payload[i + 2] == 1) return i;
                    if (payload[i + 2] == 0 && payload[i + 3] == 1) return i;
                }
            }

            return -1;
        }

        private bool IsLikelyNalHeader(byte[] payload, int index)
        {
            if (index >= payload.Length) return false;

            byte nalHeader = payload[index];
            int h264NalType = nalHeader & 0x1F;
            if (h264NalType is > 0 and < 24) return true;

            int h265NalType = (nalHeader >> 1) & 0x3F;
            return h265NalType is > 0 and < 48;
        }

        // Heuristic, used only until an SPS has been validated. The H.265 header is checked
        // first with its 2-byte constraints (layer id 0, temporal id >= 1): checking H.264
        // first misclassifies H.265 slices such as 0x02 01 (TRAIL_R) or 0x26 01 (IDR) as H.264.
        private VideoCodec DetectVideoCodec(byte[] payload)
        {
            if (payload == null || payload.Length < 5) return VideoCodec.Unknown;

            int offset = NalHeaderIndex(payload);
            if (payload.Length <= offset) return VideoCodec.Unknown;

            if (IsPlausibleNalHeader(payload, offset, VideoCodec.H265)) return VideoCodec.H265;
            if (IsPlausibleNalHeader(payload, offset, VideoCodec.H264)) return VideoCodec.H264;
            return VideoCodec.Unknown;
        }

        // ---- SPS parsers (validation + picture size) ----------------------------------------

        private sealed class BitReader
        {
            private readonly byte[] d; private int pos; private readonly int bits;
            public BitReader(byte[] data) { d = data; bits = data.Length * 8; }
            public int Read(int n)
            {
                int v = 0;
                for (int k = 0; k < n; k++)
                {
                    if (pos >= bits) throw new EndOfStreamException();
                    v = (v << 1) | ((d[pos >> 3] >> (7 - (pos & 7))) & 1);
                    pos++;
                }
                return v;
            }
            public void Skip(int n) { if (pos + n > bits) throw new EndOfStreamException(); pos += n; }
            public uint Ue()
            {
                int zeros = 0;
                while (Read(1) == 0) { if (++zeros > 31) throw new InvalidDataException(); }
                return zeros == 0 ? 0u : (uint)((1L << zeros) - 1 + Read(zeros));
            }
            public int Se() { uint k = Ue(); return (k & 1) != 0 ? (int)((k + 1) / 2) : -(int)(k / 2); }
        }

        // Removes emulation-prevention bytes (00 00 03 -> 00 00), limited to the SPS head.
        private static byte[] ToRbsp(byte[] p, int start, int len, int max = 512)
        {
            int end = start + Math.Min(len, max);
            var o = new List<byte>(end - start);
            int zeros = 0;
            for (int i = start; i < end; i++)
            {
                if (zeros >= 2 && p[i] == 3) { zeros = 0; continue; }
                o.Add(p[i]);
                zeros = p[i] == 0 ? zeros + 1 : 0;
            }
            return o.ToArray();
        }

        private static bool TryParseH265Sps(byte[] p, int nalStart, int len, out int width, out int height)
        {
            width = height = 0;
            if (len < 6) return false;
            if ((p[nalStart] & 0x81) != 0 || ((p[nalStart] >> 1) & 0x3F) != 33) return false;
            if ((p[nalStart + 1] >> 3) != 0 || (p[nalStart + 1] & 7) == 0) return false;
            try
            {
                var br = new BitReader(ToRbsp(p, nalStart + 2, len - 2));
                br.Skip(4);                                   // sps_video_parameter_set_id
                int maxSub = br.Read(3); if (maxSub > 6) return false;
                br.Skip(1);                                   // temporal_id_nesting
                if (br.Read(2) != 0) return false;            // general_profile_space
                br.Skip(1);                                   // tier
                if (br.Read(5) > 11) return false;            // general_profile_idc
                br.Skip(32 + 4 + 43 + 1);                     // compat flags, constraint flags
                br.Skip(8);                                   // general_level_idc
                var subProf = new bool[maxSub]; var subLvl = new bool[maxSub];
                for (int i = 0; i < maxSub; i++) { subProf[i] = br.Read(1) == 1; subLvl[i] = br.Read(1) == 1; }
                if (maxSub > 0) for (int i = maxSub; i < 8; i++) br.Skip(2);
                for (int i = 0; i < maxSub; i++) { if (subProf[i]) br.Skip(88); if (subLvl[i]) br.Skip(8); }
                if (br.Ue() > 15) return false;               // sps_seq_parameter_set_id
                uint chroma = br.Ue(); if (chroma > 3) return false;
                if (chroma == 3) br.Skip(1);
                uint w = br.Ue(), h = br.Ue();
                if (w == 0 || h == 0 || w > 16384 || h > 16384 || (w & 7) != 0 || (h & 7) != 0) return false;
                uint cl = 0, cr = 0, ct = 0, cb = 0;
                if (br.Read(1) == 1) { cl = br.Ue(); cr = br.Ue(); ct = br.Ue(); cb = br.Ue(); }
                if (br.Ue() > 8 || br.Ue() > 8) return false; // bit depths
                if (br.Ue() > 12) return false;               // log2_max_pic_order_cnt_lsb_minus4
                int subW = chroma is 1 or 2 ? 2 : 1, subH = chroma == 1 ? 2 : 1;
                long ww = w - (long)subW * (cl + cr), hh = h - (long)subH * (ct + cb);
                if (ww <= 0 || hh <= 0) return false;
                width = (int)ww; height = (int)hh;
                return true;
            }
            catch (Exception) { return false; }
        }

        private static bool TryParseH264Sps(byte[] p, int nalStart, int len, out int width, out int height)
        {
            width = height = 0;
            if (len < 5) return false;
            if ((p[nalStart] & 0x80) != 0 || (p[nalStart] & 0x1F) != 7 || (p[nalStart] & 0x60) == 0) return false;
            try
            {
                var br = new BitReader(ToRbsp(p, nalStart + 1, len - 1));
                int profile = br.Read(8);
                if (profile is not (66 or 77 or 88 or 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135))
                    return false;
                br.Skip(8);                                   // constraint flags
                int level = br.Read(8); if (level == 0 || level > 62) return false;
                if (br.Ue() > 31) return false;               // seq_parameter_set_id
                uint chroma = 1;
                if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
                {
                    chroma = br.Ue(); if (chroma > 3) return false;
                    if (chroma == 3) br.Skip(1);
                    if (br.Ue() > 6 || br.Ue() > 6) return false;
                    br.Skip(1);                               // qpprime_y_zero_transform_bypass
                    if (br.Read(1) == 1)                      // seq_scaling_matrix_present
                    {
                        int lists = chroma != 3 ? 8 : 12;
                        for (int i = 0; i < lists; i++)
                        {
                            if (br.Read(1) == 0) continue;
                            int size = i < 6 ? 16 : 64, last = 8, next = 8;
                            for (int j = 0; j < size; j++)
                            {
                                if (next != 0) next = (last + br.Se() + 256) % 256;
                                last = next == 0 ? last : next;
                            }
                        }
                    }
                }
                if (br.Ue() > 12) return false;               // log2_max_frame_num_minus4
                uint pocType = br.Ue(); if (pocType > 2) return false;
                if (pocType == 0) { if (br.Ue() > 12) return false; }
                else if (pocType == 1)
                {
                    br.Skip(1); br.Se(); br.Se();
                    uint n = br.Ue(); if (n > 255) return false;
                    for (int i = 0; i < n; i++) br.Se();
                }
                if (br.Ue() > 16) return false;               // max_num_ref_frames
                br.Skip(1);                                   // gaps_in_frame_num_allowed
                uint wMbs = br.Ue() + 1, hMap = br.Ue() + 1;
                int frameMbsOnly = br.Read(1);
                if (frameMbsOnly == 0) br.Skip(1);
                br.Skip(1);                                   // direct_8x8_inference
                uint cl = 0, cr = 0, ct = 0, cb = 0;
                if (br.Read(1) == 1) { cl = br.Ue(); cr = br.Ue(); ct = br.Ue(); cb = br.Ue(); }
                long w = wMbs * 16L, h = (2 - frameMbsOnly) * hMap * 16L;
                if (w > 16384 || h > 16384) return false;
                int cropX = chroma is 1 or 2 ? 2 : 1, cropY = (chroma == 1 ? 2 : 1) * (2 - frameMbsOnly);
                w -= cropX * (cl + cr); h -= cropY * (ct + cb);
                if (w <= 0 || h <= 0) return false;
                width = (int)w; height = (int)h;
                return true;
            }
            catch (Exception) { return false; }
        }

        private void DecryptVideoFrame(byte[] data, int length)
        {
            using var aes = Aes.Create();
            aes.Key = aesKey; aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None;
            using var dec = aes.CreateDecryptor();
            for (int offset = 0; offset + 64 <= length; offset += 80)
                for (int i = 0; i < 4; i++)
                    dec.TransformBlock(data, offset + i * 16, 16, data, offset + i * 16);
        }

        private void DecryptAudioFrame(byte[] data, int length)
        {
            int alignedSize = (length / 16) * 16;
            if (alignedSize == 0) return;

            using var aes = Aes.Create();
            aes.Key = aesKey;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            using var dec = aes.CreateDecryptor();
            for (int i = 0; i < alignedSize; i += 16)
                dec.TransformBlock(data, i, 16, data, i);
        }

        // Not tested
        private void DecryptMediaPre2k(byte[] data, int length, int mode)
        {
            int decryptLength;
            if (mode == 0 && length > 2048)
            {
                decryptLength = 2048;
            }
            else
            {
                decryptLength = (length / 16) * 16;
            }
            if (decryptLength > 0)
            {
                using var aes = Aes.Create();
                aes.Key = aesKey;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                using var decryptor = aes.CreateDecryptor();
                decryptor.TransformBlock(data, 0, decryptLength, data, 0);
            }
        }

        void GenerateMediaKey(uint ticket)
        {
            WriteUInt32LE(aesKey, 0, ticket);
            WriteUInt64LE(aesKey, 4, 0x618123462c14795c);
            WriteUInt32LE(aesKey, 12, 0x82800df0);
            LogUtils.debug($"[KEY] {BitConverter.ToString(aesKey).Replace("-", "")}");
        }

        byte[] GeneratePassword(string pw)
        {
            byte[] sk = Encoding.ASCII.GetBytes("macrovideo+*#!^@");
            var rng = new Random();
            byte[] rk = new byte[16];
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            for (int i = 0; i < 16; i++) rk[i] = (byte)chars[rng.Next(chars.Length)];

            var pb = Encoding.ASCII.GetBytes(pw);
            var pad = new byte[48];
            Array.Copy(pb, pad, Math.Min(pb.Length, 48));

            void Enc(byte[] key)
            {
                using var a = Aes.Create();
                a.Key = key; a.Mode = CipherMode.ECB; a.Padding = PaddingMode.None;
                using var e = a.CreateEncryptor();
                for (int i = 0; i < 48; i += 16) e.TransformBlock(pad, i, 16, pad, i);
            }
            Enc(sk); Enc(rk);

            var out_ = new byte[64];
            Array.Copy(rk, 0, out_, 0, 16);
            Array.Copy(pad, 0, out_, 16, 48);
            return out_;
        }

        bool SendData(NetworkStream s, byte[] d)
        {
            try { s.Write(d, 0, d.Length); s.Flush(); return true; }
            catch (Exception ex) { Console.Error.WriteLine($"[SEND] {ex.Message}"); return false; }
        }

        byte[] ReceiveData(NetworkStream s, int max)
        {
            var buf = new byte[max]; int tot = 0;
            var deadline = DateTime.Now.AddSeconds(5);
            while (DateTime.Now < deadline)
            {
                if (s.DataAvailable || tot > 0)
                {
                    int n = s.Read(buf, tot, max - tot);
                    if (n <= 0) break;
                    tot += n;
                    if (tot >= 16) break;
                }
                else Thread.Sleep(10);
            }
            if (tot == 0) return null;
            var r = new byte[tot]; Array.Copy(buf, r, tot); return r;
        }

        int ReadExact(NetworkStream s, byte[] buf, int off, int cnt)
        {
            int tot = 0;
            var deadline = DateTime.Now.AddSeconds(3);
            while (tot < cnt)
            {
                if (DateTime.Now > deadline)
                {
                    needReconnect = true;
                    return tot;
                }
                if (!s.DataAvailable)
                {
                    Thread.Sleep(10);
                    continue;
                }
                int n = s.Read(buf, off + tot, cnt - tot);
                if (n <= 0) break;
                tot += n;
            }
            return tot;
        }

        void WriteUInt32LE(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
        void WriteUInt16LE(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        void WriteUInt64LE(byte[] b, int o, ulong v) { for (int i = 0; i < 8; i++) b[o + i] = (byte)(v >> (i * 8)); }
        uint ReadUInt32LE(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        ushort ReadUInt16LE(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
        ulong ReadUInt64LE(byte[] b, int o) { ulong v = 0; for (int i = 0; i < 8; i++) v |= ((ulong)b[o + i]) << (i * 8); return v; }

        public bool PtzRight() => SendControl(V380Commands.PTZ_RIGHT);
        public bool PtzLeft() => SendControl(V380Commands.PTZ_LEFT);
        public bool PtzUp() => SendControl(V380Commands.PTZ_UP);
        public bool PtzDown() => SendControl(V380Commands.PTZ_DOWN);
        public bool PtzStop() => SendControl(V380Commands.PTZ_STOP);
        public bool LightOn() => SendControl(V380Commands.LIGHT_ON);
        public bool LightOff() => SendControl(V380Commands.LIGHT_OFF);
        public bool LightAuto() => SendControl(V380Commands.LIGHT_AUTO);
        public bool ImageColor() => SendControl(V380Commands.IMAGE_COLOR);
        public bool ImageBW() => SendControl(V380Commands.IMAGE_BW);
        public bool ImageAuto() => SendControl(V380Commands.IMAGE_AUTO);
        public bool ImageFlip() => SendControl(V380Commands.IMAGE_FLIP);
        private bool SendControl(byte[] payload)
        {
            if (streamStream == null) return false;
            return SendData(streamStream, payload);
        }

        public string GetDeviceId()
        {
            return deviceId.ToString();
        }

        public string GetDeviceVersion()
        {
            return deviceVersion.ToString();
        }

        public void Dispose()
        {
            authStream?.Close(); authClient?.Close();
            streamStream?.Close(); streamClient?.Close();
            snapshotManager?.Dispose();
        }
    }
}
