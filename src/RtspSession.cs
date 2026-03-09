using System.Net.Sockets;
using System.Text;

namespace V380Decoder.src
{
    public class RtspSession
    {
        private readonly int id;
        private readonly TcpClient tcp;
        private readonly NetworkStream ns;
        private readonly RtspServer server;
        private Thread readThread;
        private volatile bool playing;
        private volatile bool alive = true;

        private byte videoCh = 0;
        private byte audioCh = 2;

        private ushort videoSeq;
        private ushort audioSeq;
        private uint videoSsrc = (uint)new Random().Next();
        private uint audioSsrc = (uint)new Random().Next();
        private uint generatedVideoTimestamp;

        public event Action OnClose;

        public RtspSession(int id, TcpClient tcp, RtspServer server)
        {
            this.id = id;
            this.tcp = tcp;
            this.server = server;
            ns = tcp.GetStream();
        }

        public void Start()
        {
            readThread = new Thread(ReadLoop) { IsBackground = true, Name = $"rtsp-{id}" };
            readThread.Start();
        }

        public void Close()
        {
            alive = false;
            playing = false;
            try { tcp.Close(); } catch { }
            OnClose?.Invoke();
        }

        void ReadLoop()
        {
            var sb = new StringBuilder();
            var buf = new byte[4096];
            try
            {
                while (alive)
                {
                    int n = ns.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                    string raw = sb.ToString();
                    int end;
                    while ((end = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal)) >= 0)
                    {
                        string req = raw[..(end + 4)];
                        raw = raw[(end + 4)..];
                        HandleRequest(req);
                    }
                    sb.Clear();
                    sb.Append(raw);
                }
            }
            catch { }
            finally { Close(); }
        }

        void HandleRequest(string req)
        {
            string[] lines = req.Split("\r\n", StringSplitOptions.None);
            if (lines.Length == 0) return;

            string method = lines[0].Split(' ')[0];
            string url = lines[0].Split(' ').ElementAtOrDefault(1) ?? "";
            string cseq = lines.FirstOrDefault(l => l.StartsWith("CSeq:", StringComparison.OrdinalIgnoreCase))
                                  ?.Split(':', 2)[1].Trim() ?? "0";
            string transport = lines.FirstOrDefault(l => l.StartsWith("Transport:", StringComparison.OrdinalIgnoreCase)) ?? "";

            switch (method)
            {
                case "OPTIONS":
                    Reply(cseq, "Public: OPTIONS,DESCRIBE,SETUP,PLAY,TEARDOWN");
                    break;

                case "DESCRIBE":
                    {
                        string sdp = server.BuildSdp();
                        byte[] body = Encoding.ASCII.GetBytes(sdp);
                        Send($"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nContent-Type: application/sdp\r\nContent-Length: {body.Length}\r\n\r\n{sdp}");
                        break;
                    }

                case "SETUP":
                    {
                        bool isAudio = url.Contains("trackID=1");
                        byte ch = (byte)(isAudio ? 2 : 0);
                        var m = System.Text.RegularExpressions.Regex.Match(transport, @"interleaved=(\d+)-(\d+)");
                        if (m.Success) ch = byte.Parse(m.Groups[1].Value);

                        if (isAudio) audioCh = ch;
                        else videoCh = ch;

                        Reply(cseq,
                            $"Transport: RTP/AVP/TCP;unicast;interleaved={ch}-{ch + 1}",
                            "Session: 1");
                        break;
                    }

                case "PLAY":
                    Reply(cseq,
                        "Session: 1",
                        $"RTP-Info: url={url}/trackID=0;seq={videoSeq},url={url}/trackID=1;seq={audioSeq}");
                    playing = true;
                    Console.Error.WriteLine($"[RTSP#{id}] playing");
                    break;

                case "TEARDOWN":
                    Reply(cseq, "Session: 1");
                    Close();
                    break;

                default:
                    Send($"RTSP/1.0 501 Not Implemented\r\nCSeq: {cseq}\r\n\r\n");
                    break;
            }
        }

        void Reply(string cseq, params string[] headers)
        {
            var sb = new StringBuilder();
            sb.Append($"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\n");
            foreach (var h in headers) sb.Append(h + "\r\n");
            sb.Append("\r\n");
            Send(sb.ToString());
        }

        void Send(string s)
        {
            try
            {
                byte[] b = Encoding.ASCII.GetBytes(s);
                lock (ns) { ns.Write(b, 0, b.Length); ns.Flush(); }
            }
            catch { alive = false; }
        }

        public void PushVideo(FrameData f)
        {
            if (!playing) return;

            uint rts = f.Timestamp > 0
                ? (uint)(f.Timestamp * 90)
                : (generatedVideoTimestamp += 3600);

            RtspServer.ParseNals(f.Payload, VideoCodec.H264, (nalType, nal) =>
            {
                const int mtu = 1400;
                if (nal.Length <= mtu)
                {
                    SendRtp(videoCh, 96, videoSeq++, rts, videoSsrc, nal, 0, nal.Length, marker: true);
                    return;
                }

                SendH264Fragmented(nal, rts, mtu);
            });
        }

        void SendH264Fragmented(byte[] nal, uint rts, int mtu)
        {
            byte nalHdr = nal[0];
            byte fuInd = (byte)((nalHdr & 0xE0) | 28);
            int offset = 1;
            bool first = true;

            while (offset < nal.Length)
            {
                int chunk = Math.Min(mtu - 2, nal.Length - offset);
                bool last = offset + chunk >= nal.Length;

                byte fuHdr = (byte)(nalHdr & 0x1F);
                if (first) fuHdr |= 0x80;
                if (last) fuHdr |= 0x40;

                var frag = new byte[2 + chunk];
                frag[0] = fuInd;
                frag[1] = fuHdr;
                Array.Copy(nal, offset, frag, 2, chunk);

                SendRtp(videoCh, 96, videoSeq++, rts, videoSsrc, frag, 0, frag.Length, marker: last);
                offset += chunk;
                first = false;
            }
        }

        public void PushAudio(FrameData f)
        {
            if (!playing) return;

            uint rts = (uint)(f.Timestamp * 8);
            const int chunkSize = 160;
            for (int off = 0; off < f.Payload.Length; off += chunkSize)
            {
                int len = Math.Min(chunkSize, f.Payload.Length - off);
                SendRtp(audioCh, 8, audioSeq++, rts, audioSsrc, f.Payload, off, len, marker: false);
                rts += (uint)len;
            }
        }

        void SendRtp(byte channel, byte pt, ushort seq, uint ts, uint ssrc,
                     byte[] payload, int offset, int length, bool marker)
        {
            var rtp = new byte[12 + length];
            rtp[0] = 0x80;
            rtp[1] = (byte)((marker ? 0x80 : 0) | (pt & 0x7F));
            rtp[2] = (byte)(seq >> 8);
            rtp[3] = (byte)seq;
            rtp[4] = (byte)(ts >> 24);
            rtp[5] = (byte)(ts >> 16);
            rtp[6] = (byte)(ts >> 8);
            rtp[7] = (byte)ts;
            rtp[8] = (byte)(ssrc >> 24);
            rtp[9] = (byte)(ssrc >> 16);
            rtp[10] = (byte)(ssrc >> 8);
            rtp[11] = (byte)ssrc;
            Array.Copy(payload, offset, rtp, 12, length);

            var frame = new byte[4 + rtp.Length];
            frame[0] = 0x24;
            frame[1] = channel;
            frame[2] = (byte)(rtp.Length >> 8);
            frame[3] = (byte)rtp.Length;
            Array.Copy(rtp, 0, frame, 4, rtp.Length);

            try { lock (ns) { ns.Write(frame, 0, frame.Length); ns.Flush(); } }
            catch { alive = false; }
        }
    }
}
