using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
namespace V380Decoder.src {
	public class RtspServer {
		private readonly int port;
		private readonly H264Transcoder transcoder;
		private TcpListener listener;
		private Thread acceptThread;
		private volatile bool running;
		private readonly ConcurrentDictionary<int, RtspSession> sessions = new();
		private int nextId;
		private byte[] cachedSps;
		private byte[] cachedPps;
		private byte[] cachedVps;
		private byte[] cachedHevcSps;
		private byte[] cachedHevcPps;
		private readonly object sdpLock = new();

		public RtspServer(int port) {
			this.port = port;
			transcoder = new H264Transcoder(PushVideoDirect);
		}

		public void Start() {
			listener = new TcpListener(IPAddress.Any, port);
			listener.Start(10);
			running = true;
			acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "rtsp-accept" };
			acceptThread.Start();
			Console.Error.WriteLine($"[RTSP] rtsp://{NetworkHelper.GetLocalIPAddress()}:{port}/live");
		}

		void AcceptLoop() {
			while (running) {
				try {
					var tcp = listener.AcceptTcpClient();
					tcp.NoDelay = true;
					int id = Interlocked.Increment(ref nextId);
					var s = new RtspSession(id, tcp, this);
					sessions[id] = s;
					s.Start();
					s.OnClose += () => sessions.TryRemove(id, out _);
				} catch { }
			}
		}

		public void PushVideo(FrameData f) {
			if (f.Codec == VideoCodec.H265 && transcoder.IsAvailable) {
				transcoder.PushFrame(f);
				return;
			}
			PushVideoDirect(f);
		}

		private void PushVideoDirect(FrameData f) {
			if (f.Codec == VideoCodec.H265) {
				CacheHevcParameterSets(f.Payload);
			} else {
				CacheSpsPps(f.Payload);
			}
			foreach (var s in sessions.Values) s.PushVideo(f);
		}

		public void PushAudio(FrameData f) {
			foreach (var s in sessions.Values) s.PushAudio(f);
		}

		void CacheSpsPps(byte[] data) {
			lock (sdpLock) {
				if (cachedSps != null && cachedPps != null) return;
				ParseNals(data, VideoCodec.H264, (nalType, nal) => {
					if (nalType == 7 && cachedSps == null) cachedSps = nal;
					if (nalType == 8 && cachedPps == null) cachedPps = nal;
				});
			}
		}

		void CacheHevcParameterSets(byte[] data) {
			lock (sdpLock) {
				if (cachedVps != null && cachedHevcSps != null && cachedHevcPps != null) return;
				ParseNals(data, VideoCodec.H265, (nalType, nal) => {
					if (nalType == 32 && cachedVps == null) cachedVps = nal;
					if (nalType == 33 && cachedHevcSps == null) cachedHevcSps = nal;
					if (nalType == 34 && cachedHevcPps == null) cachedHevcPps = nal;
				});
			}
		}

		internal static void ParseNals(byte[] data, VideoCodec codec, Action<int, byte[]> cb) {
			int i = 0;
			int len = data.Length;
			while (i < len) {
				int sc = FindStartCode(data, i);
				if (sc < 0) break;
				int scLen = (sc + 3 < len && data[sc + 2] == 1) ? 3 : 4;
				int nalStart = sc + scLen;
				if (nalStart >= len) break;
				int next = FindStartCode(data, nalStart);
				int nalEnd = next < 0 ? len : next;
				int nalType = codec == VideoCodec.H265 ? (data[nalStart] >> 1) & 0x3F : data[nalStart] & 0x1F;
				var nal = new byte[nalEnd - nalStart];
				Array.Copy(data, nalStart, nal, 0, nal.Length);
				cb(nalType, nal);
				i = nalEnd;
			}
		}

		static int FindStartCode(byte[] d, int from) {
			for (int i = from; i + 3 < d.Length; i++) {
				if (d[i] == 0 && d[i + 1] == 0) {
					if (d[i + 2] == 1) return i;
					if (d[i + 2] == 0 && i + 3 < d.Length && d[i + 3] == 1) return i;
				}
			}
			return -1;
		}

		public string BuildSdp() {
		            string fmtpH264 = "";
		            string fmtpH265 = "";
		            string rtpmap = "a=rtpmap:96 H264/90000\r\n";
		            lock (sdpLock) {
		                if (cachedSps != null && cachedPps != null) {
		                    string spsB64 = Convert.ToBase64String(cachedSps);
		                    string ppsB64 = Convert.ToBase64String(cachedPps);
		                    string pli = cachedSps.Length >= 3 ? $"{cachedSps[0]:X2}{cachedSps[1]:X2}{cachedSps[2]:X2}" : "64001F";
		                    fmtpH264 = $"a=fmtp:96 packetization-mode=1;sprop-parameter-sets={spsB64},{ppsB64};profile-level-id={pli}\r\n";
		                }
		                if (cachedVps != null && cachedHevcSps != null && cachedHevcPps != null) {
		                    string vpsB64 = Convert.ToBase64String(cachedVps);
		                    string spsB64 = Convert.ToBase64String(cachedHevcSps);
		                    string ppsB64 = Convert.ToBase64String(cachedHevcPps);
		                    fmtpH265 = $"a=fmtp:97 profile-id=1;sprop-vps={vpsB64};sprop-sps={spsB64};sprop-pps={ppsB64}\r\n";
		                    rtpmap = "a=rtpmap:96 H264/90000\r\na=rtpmap:97 H265/90000\r\n";
		                }
		            }
		            LogUtils.debug($"[RTSP] SDP h264={!string.IsNullOrEmpty(fmtpH264)} hevc={!string.IsNullOrEmpty(fmtpH265)}");
		            string videoLine = !string.IsNullOrEmpty(fmtpH265) ? "m=video 0 RTP/AVP 96 97\r\n" : "m=video 0 RTP/AVP 96\r\n";
		            return "v=0\r\n" +
		                "o=- 1 1 IN IP4 0.0.0.0\r\n" +
		                "s=V380 Live\r\n" +
		                "t=0 0\r\n" +
		                "a=recvonly\r\n" +
		                videoLine +
		                rtpmap +
		                fmtpH264 +
		                fmtpH265 +
		                "a=control:trackID=0\r\n" +
		                "m=audio 0 RTP/AVP 8\r\n" +
		                "a=rtpmap:8 PCMA/8000/1\r\n" +
		                "a=control:trackID=1\r\n";
		        }

		public byte[] GetHevcParameterSets() {
			lock (sdpLock) {
				if (cachedVps == null || cachedHevcSps == null || cachedHevcPps == null) return null;
				int len = cachedVps.Length + cachedHevcSps.Length + cachedHevcPps.Length;
				var result = new byte[len];
				int offset = 0;
				Array.Copy(cachedVps, 0, result, offset, cachedVps.Length);
				offset += cachedVps.Length;
				Array.Copy(cachedHevcSps, 0, result, offset, cachedHevcSps.Length);
				offset += cachedHevcSps.Length;
				Array.Copy(cachedHevcPps, 0, result, offset, cachedHevcPps.Length);
				return result;
			}
		}

		public void Dispose() {
			running = false;
			try { listener?.Stop(); } catch { }
			foreach (var s in sessions.Values) s.Close();
			transcoder.Dispose();
		}
	}
}
