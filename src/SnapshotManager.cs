using System.Diagnostics;
using H264Sharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

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
        private H264Decoder decoder = null;
        private bool decoderInitialized = false;
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
            if (IsFFmpegAvailable())
            {
                LogUtils.debug($"[SNAPSHOT] use FFmpeg decoder");
                useFFmpeg = true;
            }
            else
            {
                LogUtils.debug($"[SNAPSHOT] use H264Sharp decoder");
                decoder = new H264Decoder();
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

                    if (codec == VideoCodec.H265 && !useFFmpeg)
                    {
                        LogUtils.debug("[SNAPSHOT] H.265 detected but FFmpeg is not available");
                        return;
                    }

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

            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (useFFmpeg)
                    DecodeWithFFmpeg(decodeFrame);
                else
                    DecodeSnapshot(decodeFrame);
            });
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

                if (codec == VideoCodec.H265 && !useFFmpeg)
                {
                    LogUtils.debug("[SNAPSHOT] H.265 detected but FFmpeg is not available");
                    return cachedJpeg;
                }

                if (isDecoding)
                    return cachedJpeg;

                h264Data = PrependSPSandPPS(lastH264Frame);
                if (h264Data == null)
                    return cachedJpeg;

                needDecode = true;
                isDecoding = true;
            }

            if (needDecode)
            {
                if (useFFmpeg)
                    DecodeWithFFmpeg(h264Data);
                else
                    DecodeSnapshot(h264Data);
            }

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

        private void DecodeSnapshot(byte[] h264Data)
        {
            try
            {
                LogUtils.debug($"[SNAPSHOT] Decoding {h264Data.Length} bytes");
                if (!decoderInitialized)
                {
                    decoder.Initialize();
                    decoderInitialized = true;
                    LogUtils.debug("[SNAPSHOT] Decoder initialized");
                }

                RgbImage rgbOut = new(ImageFormat.Bgr, imageWidth, imageHeight);

                var decodedFrame = decoder.Decode(
                    h264Data,
                    0,
                    h264Data.Length,
                    false,
                    out DecodingState state,
                    ref rgbOut
                );

                if (!decodedFrame || state != DecodingState.dsErrorFree)
                {
                    LogUtils.debug($"[SNAPSHOT] Decode failed: {state}");

                    if (state == DecodingState.dsInitialOptExpected)
                    {
                        LogUtils.debug("[SNAPSHOT] Resetting decoder...");
                        decoder.Dispose();
                        decoder = new H264Decoder();
                        decoder.Initialize();
                        decoderInitialized = true;

                        // Retry decode
                        decodedFrame = decoder.Decode(
                            h264Data, 0, h264Data.Length,
                            false, out state, ref rgbOut
                        );

                        if (!decodedFrame)
                        {
                            LogUtils.debug($"[SNAPSHOT] Retry failed: {state}");
                            lock (lockObj) { isDecoding = false; }
                            return;
                        }
                    }
                    else
                    {
                        lock (lockObj) { isDecoding = false; }
                        return;
                    }
                }

                var jpeg = ConvertToJpeg(
                    rgbOut.GetBytes(),
                    imageWidth,
                    imageHeight
                );

                StoreJpegIfUsable(jpeg);
                lock (lockObj) { isDecoding = false; }

                autoDecodeEnabled = false;
                LogUtils.debug($"[SNAPSHOT] Success: {jpeg.Length} bytes JPEG");
            }
            catch (Exception ex)
            {
                LogUtils.debug($"[SNAPSHOT] Error: {ex.Message}");
                LogUtils.debug($"[SNAPSHOT] Stack: {ex.StackTrace}");
                lock (lockObj) { isDecoding = false; }
            }
        }

        private byte[] ConvertToJpeg(byte[] rgb, int width, int height)
        {
            using var image = Image.LoadPixelData<Rgb24>(rgb, width, height);
            using var ms = new MemoryStream();
            image.Save(ms, new JpegEncoder { Quality = 80 });
            return ms.ToArray();
        }

        private void DecodeWithFFmpeg(byte[] h264Data)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-hide_banner -loglevel error -f {(codec == VideoCodec.H265 ? "hevc" : "h264")} -i pipe:0 -frames:v 1 -q:v 2 -f image2 pipe:1",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                proc.StandardInput.BaseStream.Write(h264Data, 0, h264Data.Length);
                proc.StandardInput.Close();

                using var ms = new MemoryStream();
                proc.StandardOutput.BaseStream.CopyTo(ms);
                proc.WaitForExit(2000);
                if (ms.Length > 0)
                {
                    LogUtils.debug($"[SNAPSHOT] Success: {ms.Length} bytes JPEG");
                    StoreJpegIfUsable(ms.ToArray());
                    lock (lockObj) { isDecoding = false; }
                    return;
                }
                lock (lockObj) { isDecoding = false; }
            }
            catch
            {
                Console.Error.Write("[SNAPSHOT] FFmpeg not installed");
                lock (lockObj) { isDecoding = false; }
            }
        }

        private void StoreJpegIfUsable(byte[] jpeg)
        {
            if (jpeg == null || jpeg.Length == 0)
                return;

            if (IsLikelyGrayFrame(jpeg))
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

        private bool IsLikelyGrayFrame(byte[] jpeg)
        {
            try
            {
                using var image = Image.Load<Rgb24>(jpeg);
                int stepX = Math.Max(1, image.Width / 32);
                int stepY = Math.Max(1, image.Height / 18);
                int samples = 0;
                long sum = 0;
                long sumSq = 0;
                int nearGray = 0;

                for (int y = 0; y < image.Height; y += stepY)
                {
                    for (int x = 0; x < image.Width; x += stepX)
                    {
                        var p = image[x, y];
                        int luminance = (p.R * 77 + p.G * 150 + p.B * 29) >> 8;
                        sum += luminance;
                        sumSq += luminance * luminance;
                        if (Math.Abs(p.R - p.G) < 8 && Math.Abs(p.G - p.B) < 8)
                            nearGray++;
                        samples++;
                    }
                }

                if (samples == 0)
                    return false;

                double mean = sum / (double)samples;
                double variance = (sumSq / (double)samples) - (mean * mean);
                double grayRatio = nearGray / (double)samples;

                return grayRatio > 0.96 && variance < 180;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            decoder?.Dispose();
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
