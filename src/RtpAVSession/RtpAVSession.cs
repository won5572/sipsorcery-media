﻿//-----------------------------------------------------------------------------
// Filename: RtpAVSession.cs
//
// Description: An example RTP audio/video session that can capture and render
// media on Windows.
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
// 
// History:
// 20 Feb 2020	Aaron Clauson	Created, Dublin, Ireland.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using SIPSorcery.Net;
using SIPSorcery.SIP.App;
using SIPSorcery.Sys;
using SIPSorceryMedia;

namespace SIPSorcery.Media
{
    public enum AudioSourcesEnum
    {
        None = 0,
        Microphone = 1,
        Music = 2,
        Silence = 3
    }

    public class AudioOptions
    {
        /// <summary>
        /// The type of audio source to use.
        /// </summary>
        public AudioSourcesEnum AudioSource;

        /// <summary>
        /// If using a pre-recorded audio source this is the audio source file.
        /// </summary>
        public string SourceFile;
    }

    public enum VideoSourcesEnum
    {
        None = 0,
        Webcam = 1,
        TestPattern = 2
    }

    public class VideoOptions
    {
        public const int DEFAULT_FRAME_RATE = 30;

        /// <summary>
        /// The type of video source to use.
        /// </summary>
        public VideoSourcesEnum VideoSource;

        /// <summary>
        /// IF using a video test pattern this is the base image source file.
        /// </summary>
        public string SourceFile;

        /// <summary>
        /// The frame rate to apply to request for the video source. May not be
        /// applied for certain sources such as a live webcam feed.
        /// </summary>
        public int SourceFramesPerSecond = DEFAULT_FRAME_RATE;
    }

    public class RtpAVSession : RTPSession, IMediaSession
    {
        public const string DEFAULT_AUDIO_SOURCE_FILE = "media/Macroform_-_Simplicity.ulaw";
        public static string VIDEO_TESTPATTERN = "media/testpattern.jpeg";
        public static string VIDEO_ONHOLD_TESTPATTERN = "media/testpattern_inverted.jpeg";
        public const string TELEPHONE_EVENT_ATTRIBUTE = "telephone-event";
        public const int DTMF_EVENT_DURATION = 1200;        // Default duration for a DTMF event.
        public const int DTMF_EVENT_PAYLOAD_ID = 101;
        private const int AUDIO_SAMPLE_PERIOD_MILLISECONDS = 30;

        /// <summary>
        /// PCMU encoding for silence, http://what-when-how.com/voip/g-711-compression-voip/
        /// </summary>
        private static readonly byte PCMU_SILENCE_BYTE_ZERO = 0x7F;
        private static readonly byte PCMU_SILENCE_BYTE_ONE = 0xFF;

        private static Microsoft.Extensions.Logging.ILogger Log = SIPSorcery.Sys.Log.Logger;

        private static readonly WaveFormat _waveFormat = new WaveFormat(8000, 16, 1);

        public static AudioOptions DefaultAudioOptions = new AudioOptions { AudioSource = AudioSourcesEnum.Microphone };
        public static VideoOptions DefaultVideoOptions = new VideoOptions { VideoSource = VideoSourcesEnum.None };

        private AudioOptions _audioOpts;
        private VideoOptions _videoOpts;

        /// <summary>
        /// Audio render device.
        /// </summary>
        private WaveOutEvent _waveOutEvent;

        /// <summary>
        /// Buffer for audio samples to be rendered.
        /// </summary>
        private BufferedWaveProvider _waveProvider;

        /// <summary>
        /// Audio capture device.
        /// </summary>
        private WaveInEvent _waveInEvent;

        private byte[] _currVideoFrame = new byte[65536];
        private int _currVideoFramePosn = 0;

        // Fields for decoding received RTP video packets.
        private VpxEncoder _vpxDecoder;
        private ImageConvert _imgConverter;

        /// <summary>
        /// Dummy video source which supplies a test pattern with a rolling 
        /// timestamp.
        /// </summary>
        private TestPatternVideoSource _testPatternVideoSource;
        private StreamReader _audioStreamReader;
        private Timer _audioStreamTimer;

        private uint _rtpAudioTimestampPeriod = 0;
        private uint _rtpVideoTimestampPeriod = 0;
        private bool _isStarted = false;
        private bool _isClosed = false;

        /// <summary>
        /// Fired when a video sample is ready for rendering.
        /// [sample, width, height, stride].
        /// </summary>
        public event Action<byte[], uint, uint, int> OnVideoSampleReady;

        /// <summary>
        /// Creates a new RTP audio visual session with audio/video capturing and rendering capabilities.
        /// Uses default options for audio and video.
        /// </summary>
        /// <param name="addrFamily">The address family to create the underlying socket on (IPv4 or IPv6).</param>
        public RtpAVSession(AddressFamily addrFamily) :
           this(addrFamily, DefaultAudioOptions, DefaultVideoOptions)
        { }

        /// <summary>
        /// Creates a new RTP audio visual session with audio/video capturing and rendering capabilities.
        /// </summary>
        /// <param name="addrFamily">The address family to create the underlying socket on (IPv4 or IPv6).</param>
        /// <param name="audioOptions">Options for the send and receive audio streams on this session.</param>
        /// <param name="videoOptions">Options for the send and receive video streams on this session</param>
        public RtpAVSession(AddressFamily addrFamily, AudioOptions audioOptions, VideoOptions videoOptions)
            : base(addrFamily, false, false, false)
        {
            _audioOpts = audioOptions ?? DefaultAudioOptions;
            _videoOpts = videoOptions ?? DefaultVideoOptions;

            // Initialise the video decoding objects. Even if we are not sourcing video
            // we need to be ready to receive and render.
            _vpxDecoder = new VpxEncoder();
            int res = _vpxDecoder.InitDecoder();
            if (res != 0)
            {
                throw new ApplicationException("VPX decoder initialisation failed.");
            }
            _imgConverter = new ImageConvert();

            if (_audioOpts.AudioSource != AudioSourcesEnum.None)
            {
                var pcmu = new SDPMediaFormat(SDPMediaFormatsEnum.PCMU);

                // RTP event support.
                int clockRate = pcmu.GetClockRate();
                SDPMediaFormat rtpEventFormat = new SDPMediaFormat(DTMF_EVENT_PAYLOAD_ID);
                rtpEventFormat.SetFormatAttribute($"{TELEPHONE_EVENT_ATTRIBUTE}/{clockRate}");
                rtpEventFormat.SetFormatParameterAttribute("0-16");

                var audioCapabilities = new List<SDPMediaFormat> { pcmu, rtpEventFormat };

                MediaStreamTrack audioTrack = new MediaStreamTrack(null, SDPMediaTypesEnum.audio, false, audioCapabilities);
                addTrack(audioTrack);
            }

            if (_videoOpts.VideoSource != VideoSourcesEnum.None)
            {
                MediaStreamTrack videoTrack = new MediaStreamTrack(null, SDPMediaTypesEnum.video, false, new List<SDPMediaFormat> { new SDPMediaFormat(SDPMediaFormatsEnum.VP8) });
                addTrack(videoTrack);
            }

            // Where the magic (for processing received media) happens.
            base.OnRtpPacketReceived += RtpPacketReceived;
        }

        /// <summary>
        /// Sets or updates the sources of the audio and/or video streams. 
        /// </summary>
        /// <param name="audioOptions">Optional. If audio is being switched the new source options.
        /// Set to null to leave the audio source unchanged.</param>
        /// <param name="videoOptions">Optional. If video is being switched the new source options.
        /// Set to null to leave the video source unchanged.</param>
        public async Task SetSources(AudioOptions audioOptions, VideoOptions videoOptions)
        {
            if (audioOptions == null)
            {
                // Do nothing, audio source not being changed.
            }
            else if (audioOptions.AudioSource == AudioSourcesEnum.None)
            {
                // Audio source no longer required.
                _waveInEvent?.StopRecording();

                if (_audioStreamTimer != null)
                {
                    _audioStreamTimer?.Dispose();

                    // Give any currently executing audio sampling time to complete.
                    await Task.Delay(AUDIO_SAMPLE_PERIOD_MILLISECONDS * 2).ConfigureAwait(false);
                }

                _audioStreamReader.Close();
                _audioOpts = audioOptions;
            }
            else
            {
                SetAudioSource(audioOptions);
                _audioOpts = audioOptions;
                StartAudio();
            }

            if (videoOptions == null)
            {
                // Do nothing, video source not being changed.
            }
            else if (videoOptions.VideoSource == VideoSourcesEnum.None)
            {
                // Video source no longer required.
                _testPatternVideoSource?.Stop();
                _videoOpts = videoOptions;
            }
            else
            {
                await SetVideoSource(videoOptions).ConfigureAwait(false);
                _videoOpts = videoOptions;
                StartVideo();
            }
        }

        /// <summary>
        /// Starts the media capturing/source devices.
        /// </summary>
        public async Task Start()
        {
            if (!_isStarted)
            {
                _isStarted = true;

                if (_audioOpts.AudioSource != AudioSourcesEnum.None)
                {
                    SetAudioSource(_audioOpts);
                    StartAudio();
                }

                if (_videoOpts.VideoSource != VideoSourcesEnum.None)
                {
                    await SetVideoSource(_videoOpts).ConfigureAwait(false);
                    StartVideo();
                }
            }
        }

        /// <summary>
        /// Sends a DTMF tone as an RTP event to the remote party.
        /// </summary>
        /// <param name="key">The DTMF tone to send.</param>
        /// <param name="ct">RTP events can span multiple RTP packets. This token can
        /// be used to cancel the send.</param>
        public Task SendDtmf(byte key, CancellationToken ct)
        {
            var dtmfEvent = new RTPEvent(key, false, RTPEvent.DEFAULT_VOLUME, DTMF_EVENT_DURATION, DTMF_EVENT_PAYLOAD_ID);
            return SendDtmfEvent(dtmfEvent, ct);
        }

        /// <summary>
        /// Send a media sample via RTP.
        /// </summary>
        /// <param name="mediaType">The type of media to send (audio or video).</param>
        /// <param name="samplePeriod">The period measured in sampling units for the sample.</param>
        /// <param name="sample">The raw sample.</param>
        public void SendMedia(SDPMediaTypesEnum mediaType, uint samplePeriod, byte[] sample)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Sets relevant properties for this session based on the SDP from the remote party.
        /// </summary>
        /// <param name="sessionDescription">The session description from the remote call party.</param>
        public override void setRemoteDescription(RTCSessionDescription sessionDescription)
        {
            base.setRemoteDescription(sessionDescription);

            var connAddr = IPAddress.Parse(sessionDescription.sdp.Connection.ConnectionAddress);

            foreach (var announcement in sessionDescription.sdp.Media)
            {
                var annAddr = connAddr;
                if (announcement.Connection != null)
                {
                    annAddr = IPAddress.Parse(announcement.Connection.ConnectionAddress);
                }

                if (announcement.Media == SDPMediaTypesEnum.audio)
                {
                    var connRtpEndPoint = new IPEndPoint(annAddr, announcement.Port);
                    var connRtcpEndPoint = new IPEndPoint(annAddr, announcement.Port + 1);

                    SetDestination(SDPMediaTypesEnum.audio, connRtpEndPoint, connRtcpEndPoint);

                    foreach (var mediaFormat in announcement.MediaFormats)
                    {
                        if (mediaFormat.FormatAttribute?.StartsWith(TELEPHONE_EVENT_ATTRIBUTE) == true)
                        {
                            if (!int.TryParse(mediaFormat.FormatID, out var remoteRtpEventPayloadID))
                            {
                                //logger.LogWarning("The media format on the telephone event attribute was not a valid integer.");
                            }
                            else
                            {
                                base.RemoteRtpEventPayloadID = remoteRtpEventPayloadID;
                            }
                            break;
                        }
                    }
                }
                else if (announcement.Media == SDPMediaTypesEnum.video)
                {
                    var connRtpEndPoint = new IPEndPoint(annAddr, announcement.Port);
                    var connRtcpEndPoint = new IPEndPoint(annAddr, announcement.Port + 1);

                    SetDestination(SDPMediaTypesEnum.video, connRtpEndPoint, connRtcpEndPoint);
                }
            }
        }

        /// <summary>
        /// Closes the session.
        /// </summary>
        /// <param name="reason">Reason for the closure.</param>
        public void Close(string reason)
        {
            if (!_isClosed)
            {
                _isClosed = true;

                base.OnRtpPacketReceived -= RtpPacketReceived;

                _waveOutEvent?.Stop();

                if (_waveInEvent != null)
                {
                    _waveInEvent.DataAvailable -= LocalAudioSampleAvailable;
                    _waveInEvent.StopRecording();
                }

                _audioStreamTimer?.Dispose();

                if (_testPatternVideoSource != null)
                {
                    _testPatternVideoSource.SampleReady -= LocalVideoSampleAvailable;
                    _testPatternVideoSource.Stop();
                    _testPatternVideoSource.Dispose();
                }

                // The VPX encoder is a memory hog. 
                _vpxDecoder.Dispose();
                _imgConverter.Dispose();

                base.CloseSession(reason);
            }
        }

        /// <summary>
        /// Initialise the audio capture and render device.
        /// </summary>
        private void SetAudioSource(AudioOptions audioSourceOpts)
        {
            // Render device.
            if (_waveOutEvent == null)
            {
                _waveOutEvent = new WaveOutEvent();
                _waveProvider = new BufferedWaveProvider(_waveFormat);
                _waveProvider.DiscardOnBufferOverflow = true;
                _waveOutEvent.Init(_waveProvider);
            }

            // Audio source.
            if (audioSourceOpts.AudioSource == AudioSourcesEnum.Microphone)
            {
                if (_waveInEvent == null)
                {
                    if (WaveInEvent.DeviceCount > 0)
                    {
                        _waveInEvent = new WaveInEvent();
                        _waveInEvent.BufferMilliseconds = AUDIO_SAMPLE_PERIOD_MILLISECONDS;
                        _waveInEvent.NumberOfBuffers = 1;
                        _waveInEvent.DeviceNumber = 0;
                        _waveInEvent.WaveFormat = _waveFormat;
                        _waveInEvent.DataAvailable += LocalAudioSampleAvailable;
                    }
                    else
                    {
                        Log.LogWarning("No audio capture devices are available. No audio stream will be sent.");
                    }
                }
            }
            else if (audioSourceOpts.AudioSource == AudioSourcesEnum.Music)
            {
                string newAudioFile = audioSourceOpts.SourceFile ?? DEFAULT_AUDIO_SOURCE_FILE;

                // Check whether this is the initial load or whether the source file is the same. If it is there's no need to do anything.
                if (_audioStreamReader == null || newAudioFile != _audioOpts.SourceFile)
                {
                    if (!File.Exists(newAudioFile))
                    {
                        if (File.Exists(DEFAULT_AUDIO_SOURCE_FILE))
                        {
                            Log.LogWarning($"The requested audio source file could not be found {newAudioFile}, falling back to default.");
                            newAudioFile = DEFAULT_AUDIO_SOURCE_FILE;
                        }
                        else
                        {
                            Log.LogError($"The requested audio source file could not be found {newAudioFile}, no audio source will be initialised.");
                            newAudioFile = null;
                        }
                    }

                    if (newAudioFile != null)
                    {
                        _audioStreamReader = new StreamReader(newAudioFile);
                    }
                }
            }

            if (_rtpAudioTimestampPeriod == 0)
            {
                _rtpAudioTimestampPeriod = (uint)(SDPMediaFormatInfo.GetClockRate(SDPMediaFormatsEnum.PCMU) / AUDIO_SAMPLE_PERIOD_MILLISECONDS);
            }
        }

        /// <summary>
        /// Once the audio devices have been initialised this method needs to be called to start the
        /// audio rendering device and if a source has been selected it will also get started.
        /// </summary>
        private void StartAudio()
        {
            // Audio rendering (speaker).
            if (_waveOutEvent != null && _waveOutEvent.PlaybackState != PlaybackState.Playing)
            {
                _waveOutEvent.Play();
            }

            // If required start the audio source.
            if (_audioOpts != null && _audioOpts.AudioSource != AudioSourcesEnum.None)
            {
                _waveInEvent?.StopRecording();

                if (_audioOpts.AudioSource == AudioSourcesEnum.Microphone)
                {
                    // Don't need the stream or silence sampling.
                    if (_audioStreamTimer != null)
                    {
                        _audioStreamTimer?.Dispose();
                    }

                    try
                    {
                        _waveInEvent.StartRecording();
                    }
                    // Even though we've requested a recording be stopped this call occasionally 
                    // throws saying recording has already started.
                    catch (Exception excp) 
                    {
                        Log.LogDebug($"Exception was thrown starting microphone, should be safe to ignore. {excp.Message}");
                    } 
                }
                else if (_audioOpts.AudioSource == AudioSourcesEnum.Silence)
                {
                    _audioStreamTimer = new Timer(SendSilenceSample, null, 0, AUDIO_SAMPLE_PERIOD_MILLISECONDS);
                }
                else if (_audioOpts.AudioSource == AudioSourcesEnum.Music)
                {
                    if (_audioStreamReader == null)
                    {
                        Log.LogWarning("Could not start audio music source as the file stream reader was null.");
                    }
                    else
                    {
                        _audioStreamTimer = new Timer(SendMusicSample, null, 0, AUDIO_SAMPLE_PERIOD_MILLISECONDS);
                    }
                }
            }
        }

        /// <summary>
        /// Initialise the video capture and render device.
        /// </summary>
        private async Task SetVideoSource(VideoOptions videoSourceOpts)
        {
            if (videoSourceOpts.VideoSource == VideoSourcesEnum.TestPattern)
            {
                if (_testPatternVideoSource == null)
                {
                    _testPatternVideoSource = new TestPatternVideoSource(videoSourceOpts.SourceFile, videoSourceOpts.SourceFramesPerSecond);
                    _testPatternVideoSource.SampleReady += LocalVideoSampleAvailable;
                }
                else
                {
                    await _testPatternVideoSource.SetSource(videoSourceOpts.SourceFile, videoSourceOpts.SourceFramesPerSecond).ConfigureAwait(false);
                }

                if (_testPatternVideoSource.FramesPerSecond != 0)
                {
                    _rtpVideoTimestampPeriod = (uint)(SDPMediaFormatInfo.GetClockRate(SDPMediaFormatsEnum.VP8) / _testPatternVideoSource.FramesPerSecond);
                }
                else
                {
                    _rtpVideoTimestampPeriod = (uint)(SDPMediaFormatInfo.GetClockRate(SDPMediaFormatsEnum.VP8) / TestPatternVideoSource.DEFAULT_FRAMES_PER_SECOND);
                }
            }
        }

        /// <summary>
        /// Once the video source has been initialised this method needs to be called to start it.
        /// </summary>
        private void StartVideo()
        {
            if (_videoOpts.VideoSource == VideoSourcesEnum.TestPattern && _testPatternVideoSource != null)
            {
                _testPatternVideoSource.Start();
            }
        }

        /// <summary>
        /// Event handler for audio sample being supplied by local capture device.
        /// </summary>
        private void LocalAudioSampleAvailable(object sender, WaveInEventArgs args)
        {
            byte[] sample = new byte[args.Buffer.Length / 2];
            int sampleIndex = 0;

            for (int index = 0; index < args.BytesRecorded; index += 2)
            {
                var ulawByte = NAudio.Codecs.MuLawEncoder.LinearToMuLawSample(BitConverter.ToInt16(args.Buffer, index));
                sample[sampleIndex++] = ulawByte;
            }

            base.SendAudioFrame((uint)sample.Length, (int)SDPMediaFormatsEnum.PCMU, sample);
        }

        /// <summary>
        /// Event handler for video sample being supplied by local capture device.
        /// </summary>
        private void LocalVideoSampleAvailable(byte[] sample)
        {
            base.SendVp8Frame(_rtpVideoTimestampPeriod, (int)SDPMediaFormatsEnum.VP8, sample);
        }

        /// <summary>
        /// Event handler for receiving RTP packets from a remote party.
        /// </summary>
        /// <param name="mediaType">The media type of the packets.</param>
        /// <param name="rtpPacket">The RTP packet with the media sample.</param>
        private void RtpPacketReceived(SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
        {
            //Log.LogDebug($"RTP packet received for {mediaType}.");

            if (mediaType == SDPMediaTypesEnum.audio)
            {
                RenderAudio(rtpPacket);
            }
            else if (mediaType == SDPMediaTypesEnum.video)
            {
                RenderVideo(rtpPacket);
            }
        }

        /// <summary>
        /// Render an audio RTP packet received from a remote party.
        /// </summary>
        /// <param name="rtpPacket">The RTP packet containing the audio payload.</param>
        private void RenderAudio(RTPPacket rtpPacket)
        {
            if (_waveProvider != null)
            {
                var sample = rtpPacket.Payload;
                for (int index = 0; index < sample.Length; index++)
                {
                    short pcm = NAudio.Codecs.MuLawDecoder.MuLawToLinearSample(sample[index]);
                    byte[] pcmSample = new byte[] { (byte)(pcm & 0xFF), (byte)(pcm >> 8) };
                    _waveProvider.AddSamples(pcmSample, 0, 2);
                }
            }
        }

        /// <summary>
        /// Render a video RTP packet received from a remote party.
        /// </summary>
        /// <param name="rtpPacket">The RTP packet containing the video payload.</param>
        private void RenderVideo(RTPPacket rtpPacket)
        {
            if (_currVideoFramePosn > 0 || (rtpPacket.Payload[0] & 0x10) > 0)
            {
                RtpVP8Header vp8Header = RtpVP8Header.GetVP8Header(rtpPacket.Payload);
                Buffer.BlockCopy(rtpPacket.Payload, vp8Header.Length, _currVideoFrame, _currVideoFramePosn, rtpPacket.Payload.Length - vp8Header.Length);
                _currVideoFramePosn += rtpPacket.Payload.Length - vp8Header.Length;

                if (rtpPacket.Header.MarkerBit == 1)
                {
                    unsafe
                    {
                        fixed (byte* p = _currVideoFrame)
                        {
                            uint width = 0, height = 0;
                            byte[] i420 = null;

                            //Console.WriteLine($"Attempting vpx decode {_currVideoFramePosn} bytes.");

                            int decodeResult = _vpxDecoder.Decode(p, _currVideoFramePosn, ref i420, ref width, ref height);

                            if (decodeResult != 0)
                            {
                                Console.WriteLine("VPX decode of video sample failed.");
                            }
                            else
                            {
                                if (OnVideoSampleReady != null)
                                {
                                    fixed (byte* r = i420)
                                    {
                                        byte[] bmp = null;
                                        int stride = 0;
                                        int convRes = _imgConverter.ConvertYUVToRGB(r, VideoSubTypesEnum.I420, (int)width, (int)height, VideoSubTypesEnum.BGR24, ref bmp, ref stride);

                                        if (convRes == 0)
                                        {
                                            //fixed (byte* s = bmp)
                                            //{
                                            //    System.Drawing.Bitmap bmpImage = new System.Drawing.Bitmap((int)width, (int)height, stride, System.Drawing.Imaging.PixelFormat.Format24bppRgb, (IntPtr)s);
                                            //}
                                            OnVideoSampleReady(bmp, width, height, stride);
                                        }
                                        else
                                        {
                                            Log.LogWarning("Pixel format conversion of decoded sample failed.");
                                        }
                                    }
                                }
                            }
                        }
                    }

                    _currVideoFramePosn = 0;
                }
            }
            else
            {
                Log.LogWarning("Discarding RTP packet, VP8 header Start bit not set.");
                Log.LogWarning($"rtp video, seqnum {rtpPacket.Header.SequenceNumber}, ts {rtpPacket.Header.Timestamp}, marker {rtpPacket.Header.MarkerBit}, payload {rtpPacket.Payload.Length}, payload[0-5] {rtpPacket.Payload.HexStr(5)}.");
            }
        }

        /// <summary>
        /// Sends audio samples read from a file.
        /// </summary>
        private void SendMusicSample(object state)
        {
            int sampleSize = (SDPMediaFormatInfo.GetClockRate(SDPMediaFormatsEnum.PCMU) / 1000) * AUDIO_SAMPLE_PERIOD_MILLISECONDS;
            byte[] sample = new byte[sampleSize];
            int bytesRead = _audioStreamReader.BaseStream.Read(sample, 0, sample.Length);

            if (bytesRead == 0 || _audioStreamReader.EndOfStream)
            {
                _audioStreamReader.BaseStream.Position = 0;
                bytesRead = _audioStreamReader.BaseStream.Read(sample, 0, sample.Length);
            }

            SendAudioFrame((uint)bytesRead, (int)SDPMediaFormatsEnum.PCMU, sample.Take(bytesRead).ToArray());
        }

        /// <summary>
        /// Sends the sounds of silence.
        /// </summary>
        private void SendSilenceSample(object state)
        {
            uint bufferSize = (uint)AUDIO_SAMPLE_PERIOD_MILLISECONDS;

            byte[] sample = new byte[bufferSize / 2];
            int sampleIndex = 0;

            for (int index = 0; index < bufferSize; index += 2)
            {
                sample[sampleIndex] = PCMU_SILENCE_BYTE_ZERO;
                sample[sampleIndex + 1] = PCMU_SILENCE_BYTE_ONE;
            }

            SendAudioFrame(bufferSize, (int)SDPMediaFormatsEnum.PCMU, sample);
        }
    }
}
