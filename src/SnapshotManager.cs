using System.Diagnostics;

namespace V380Decoder.src
{

    public class SnapshotManager : IDisposable
    {
        private enum VideoCodec
        {
            Unknown,
            H264,
            H265
        }

        private byte[] vpsNal = null;
        private byte[] spsNal = null;
        private byte[] ppsNal = null;
        private byte[] lastH264Frame = null;
        private byte[] cachedJpeg = null;
        private DateTime lastDecodeTime = DateTime.MinValue;
        private long cachedJpegVersion = 0;
        private readonly object lockObj = new object();
        private bool isDecoding = false;
        private int imageWidth;
        private int imageHeight;
        private bool autoDecodeEnabled = true;
        private DateTime lastAutoDecodeTime = DateTime.MinValue;
        private const int MinAutoDecodeIntervalH264Ms = 2000;
        private const int MinAutoDecodeIntervalFFmpegMs = 200;
        private const int CachedSnapshotLifetimeH264Ms = 5000;
        private const int CachedSnapshotLifetimeFFmpegMs = 750;
        private bool useFFmpeg = false;
        private VideoCodec codec = VideoCodec.Unknown;

        public SnapshotManager()
        {
            // Snapshots use ffmpeg only (ImageSharp and H264Sharp removed).
            if (IsFFmpegAvailable())
            {
                LogUtils.debug($"[SNAPSHOT] use FFmpeg decoder");
                useFFmpeg = true;
            }
            else
            {
                Console.Error.WriteLine("[SNAPSHOT] ffmpeg not found: snapshots and MJPEG disabled");
            }
        }

        private bool IsFFmpegAvailable()
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = "-version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                bool exited = process.WaitForExit(2000);
                return exited && process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public void UpdateFrame(byte[] h264Frame, int width, int height, bool isKeyFrame, ulong timestamp)
        {
            bool shouldQueueDecode = false;
            byte[] decodeFrame = null;

            lock (lockObj)
            {
                imageWidth = width;
                imageHeight = height;

                if (spsNal == null || ppsNal == null)
                {
                    ExtractSPSandPPS(h264Frame);
                }

                if (isKeyFrame)
                {
                    lastH264Frame = (byte[])h264Frame.Clone();
                }
            }

            if (!isKeyFrame)
                return;

            if (autoDecodeEnabled)
            {
                lock (lockObj)
                {
                    int minIntervalMs = useFFmpeg ? MinAutoDecodeIntervalFFmpegMs : MinAutoDecodeIntervalH264Ms;
                    if ((DateTime.Now - lastAutoDecodeTime).TotalMilliseconds < minIntervalMs)
                        return;

                    if (isDecoding)
                        return;

                    if (!useFFmpeg)
                        return;

                    if (spsNal == null || ppsNal == null)
                    {
                        LogUtils.debug("[SNAPSHOT] Skipping auto decode: SPS/PPS not available yet");
                        return;
                    }

                    decodeFrame = PrependSPSandPPS(lastH264Frame);
                    if (decodeFrame == null)
                        return;

                    isDecoding = true;
                    lastAutoDecodeTime = DateTime.Now;
                    shouldQueueDecode = true;
                }
            }

            if (!shouldQueueDecode)
                return;

            ThreadPool.QueueUserWorkItem(_ => DecodeWithFFmpeg(decodeFrame));
        }

        private void ExtractSPSandPPS(byte[] h264Data)
        {
            var nalUnits = FindNalUnits(h264Data);

            foreach (var nal in nalUnits)
            {
                if (nal.Length == 0) continue;

                int h264NalType = nal[0] & 0x1F;
                int h265NalType = (nal[0] >> 1) & 0x3F;

                if (h264NalType == 7) // SPS
                {
                    codec = VideoCodec.H264;
                    spsNal = (byte[])nal.Clone();
                    LogUtils.debug($"[SNAPSHOT] Found SPS: {spsNal.Length} bytes");
                }
                else if (h264NalType == 8) // PPS
                {
                    codec = VideoCodec.H264;
                    ppsNal = (byte[])nal.Clone();
                    LogUtils.debug($"[SNAPSHOT] Found PPS: {ppsNal.Length} bytes");
                }
                else if (h265NalType == 32) // VPS
                {
                    codec = VideoCodec.H265;
                    vpsNal = (byte[])nal.Clone();
                    LogUtils.debug($"[SNAPSHOT] Found VPS (H.265): {vpsNal.Length} bytes");
                }
                else if (h265NalType == 33) // SPS
                {
                    codec = VideoCodec.H265;
                    spsNal = (byte[])nal.Clone();
                    LogUtils.debug($"[SNAPSHOT] Found SPS (H.265): {spsNal.Length} bytes");
                }
                else if (h265NalType == 34) // PPS
                {
                    codec = VideoCodec.H265;
                    ppsNal = (byte[])nal.Clone();
                    LogUtils.debug($"[SNAPSHOT] Found PPS (H.265): {ppsNal.Length} bytes");
                }
            }
        }

        private List<byte[]> FindNalUnits(byte[] h264Data)
        {
            var nalUnits = new List<byte[]>();
            int i = 0;

            while (i < h264Data.Length - 4)
            {
                if (h264Data[i] == 0 && h264Data[i + 1] == 0 &&
                    h264Data[i + 2] == 0 && h264Data[i + 3] == 1)
                {
                    int start = i + 4;
                    int end = start;

                    while (end < h264Data.Length - 4)
                    {
                        if (h264Data[end] == 0 && h264Data[end + 1] == 0 &&
                            h264Data[end + 2] == 0 && h264Data[end + 3] == 1)
                        {
                            break;
                        }
                        end++;
                    }

                    if (end == h264Data.Length - 4)
                        end = h264Data.Length;

                    byte[] nal = new byte[end - start];
                    Array.Copy(h264Data, start, nal, 0, nal.Length);
                    nalUnits.Add(nal);

                    i = end;
                }
                else
                {
                    i++;
                }
            }

            return nalUnits;
        }

        public byte[] GetSnapshot(int timeoutMs = 5000)
        {
            byte[] h264Data;
            bool needDecode;

            lock (lockObj)
            {
                int cachedLifetimeMs = useFFmpeg ? CachedSnapshotLifetimeFFmpegMs : CachedSnapshotLifetimeH264Ms;
                if (cachedJpeg != null &&
                    (DateTime.Now - lastDecodeTime).TotalMilliseconds < cachedLifetimeMs)
                {
                    return cachedJpeg;
                }

                if (cachedJpeg != null && isDecoding)
                {
                    return cachedJpeg;
                }

                if (lastH264Frame == null || spsNal == null || ppsNal == null)
                {
                    LogUtils.debug("[SNAPSHOT] Missing frame or SPS/PPS");
                    return cachedJpeg;
                }

                if (!useFFmpeg)
                    return cachedJpeg;

                if (isDecoding)
                    return cachedJpeg;

                h264Data = PrependSPSandPPS(lastH264Frame);
                if (h264Data == null)
                    return cachedJpeg;

                needDecode = true;
                isDecoding = true;
            }

            if (needDecode)
                DecodeWithFFmpeg(h264Data);

            lock (lockObj)
            {
                return cachedJpeg;
            }
        }

        public byte[] GetCachedSnapshot()
        {
            lock (lockObj)
            {
                return cachedJpeg;
            }
        }

        public bool TryGetCachedSnapshot(out byte[] jpeg, out long version, out DateTime timestamp)
        {
            lock (lockObj)
            {
                jpeg = cachedJpeg;
                version = cachedJpegVersion;
                timestamp = lastDecodeTime;
                return jpeg != null && jpeg.Length > 0;
            }
        }

        private byte[] PrependSPSandPPS(byte[] idrFrame)
        {
            if (idrFrame == null || spsNal == null || ppsNal == null)
                return null;

            byte[] startCode = new byte[] { 0x00, 0x00, 0x00, 0x01 };

            using var ms = new MemoryStream();
            if (codec == VideoCodec.H265 && vpsNal != null)
            {
                ms.Write(startCode, 0, 4);
                ms.Write(vpsNal, 0, vpsNal.Length);
            }
            ms.Write(startCode, 0, 4);
            ms.Write(spsNal, 0, spsNal.Length);
            ms.Write(startCode, 0, 4);
            ms.Write(ppsNal, 0, ppsNal.Length);
            ms.Write(idrFrame, 0, idrFrame.Length);

            return ms.ToArray();
        }

        // One ffmpeg run per snapshot: the JPEG on stdout and, for the
        // gray-frame test, a raw RGB 32x18 thumbnail (nearest-neighbour scaling)
        // in a temp file. No image is decoded in-process.
        private const int ThumbW = 32, ThumbH = 18;
        private const int FFmpegTimeoutMs = 5000;

        private void DecodeWithFFmpeg(byte[] h264Data)
        {
            string thumbPath = Path.Combine(Path.GetTempPath(), $"v380-thumb-{Environment.ProcessId}-{Guid.NewGuid():N}.rgb");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                foreach (var a in new[] {
                    "-hide_banner", "-loglevel", "error",
                    "-f", codec == VideoCodec.H265 ? "hevc" : "h264", "-i", "pipe:0",
                    "-filter_complex", $"[0:v]split=2[j][g];[g]scale={ThumbW}:{ThumbH}:flags=neighbor,format=rgb24[t]",
                    "-map", "[j]", "-frames:v", "1", "-q:v", "2", "-f", "image2", "pipe:1",
                    "-map", "[t]", "-frames:v", "1", "-f", "rawvideo", "-y", thumbPath })
                    psi.ArgumentList.Add(a);

                using var proc = Process.Start(psi);
                // Write stdin while reading stdout to avoid a pipe deadlock.
                var writer = Task.Run(() =>
                {
                    try
                    {
                        proc.StandardInput.BaseStream.Write(h264Data, 0, h264Data.Length);
                        proc.StandardInput.Close();
                    }
                    catch (IOException) { }
                });

                using var ms = new MemoryStream();
                var reader = proc.StandardOutput.BaseStream.CopyToAsync(ms);
                bool timedOut = false;
                if (!reader.Wait(FFmpegTimeoutMs) || !proc.WaitForExit(FFmpegTimeoutMs))
                {
                    timedOut = true;
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    Console.Error.WriteLine("[SNAPSHOT] ffmpeg timeout: frame ignored");
                }
                writer.Wait(1000);

                if (!timedOut && ms.Length > 0)
                {
                    LogUtils.debug($"[SNAPSHOT] Success: {ms.Length} bytes JPEG");
                    byte[] thumb = File.Exists(thumbPath) ? File.ReadAllBytes(thumbPath) : null;
                    StoreJpegIfUsable(ms.ToArray(), thumb);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SNAPSHOT] ffmpeg error: {ex.Message}");
            }
            finally
            {
                try { File.Delete(thumbPath); } catch { }
                lock (lockObj) { isDecoding = false; }
            }
        }

        private void StoreJpegIfUsable(byte[] jpeg, byte[] thumbRgb)
        {
            if (jpeg == null || jpeg.Length == 0)
                return;

            if (IsLikelyGrayFrame(thumbRgb))
            {
                LogUtils.debug("[SNAPSHOT] Ignored low-detail gray frame");
                return;
            }

            lock (lockObj)
            {
                cachedJpeg = jpeg;
                lastDecodeTime = DateTime.Now;
                cachedJpegVersion++;
            }
        }

        // Same thresholds as the former ImageSharp code: almost all gray AND very little detail.
        // Missing or incomplete thumbnail: frame accepted, as the old code did on error.
        private static bool IsLikelyGrayFrame(byte[] rgb)
        {
            if (rgb == null || rgb.Length < ThumbW * ThumbH * 3)
                return false;

            int samples = 0, nearGray = 0;
            long sum = 0, sumSq = 0;
            for (int i = 0; i + 2 < ThumbW * ThumbH * 3; i += 3)
            {
                int r = rgb[i], g = rgb[i + 1], b = rgb[i + 2];
                int luminance = (r * 77 + g * 150 + b * 29) >> 8;
                sum += luminance;
                sumSq += luminance * luminance;
                if (Math.Abs(r - g) < 8 && Math.Abs(g - b) < 8)
                    nearGray++;
                samples++;
            }

            double mean = sum / (double)samples;
            double variance = (sumSq / (double)samples) - (mean * mean);
            double grayRatio = nearGray / (double)samples;

            return grayRatio > 0.96 && variance < 180;
        }

        public void Dispose()
        {
            lock (lockObj)
            {
                lastH264Frame = null;
                cachedJpeg = null;
                vpsNal = null;
                spsNal = null;
                ppsNal = null;
                codec = VideoCodec.Unknown;
            }
        }
    }
}
