using System.Collections;
using UnityEngine;
using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine.Networking;
using Pv.Unity;
using System.Collections.Generic;

public class CaptureAudio : MonoBehaviour
{
    [SerializeField]
    private GameObject _activeRecordingIndicator;
    [SerializeField]
    private float _flashInterval = 0.1f; // Time interval for flashing the indicator (in seconds)
    [SerializeField]
    private float _silenceThreshold = 0.07f;
    [SerializeField]
    private float _silenceCheckInterval = 0.5f;
    [SerializeField]
    private float _minimalAudioDuration = 2.0f;

    CapturePhoto _capturePhoto;
    WebSocketClient _webSocketClient;
    UserFeedback _userFeedback;
    AudioSource _audioSource;
    Porcupine _porcupine;

    // Audio recording
    enum RecordingState
    {
        Idle,
        EarlyRecording,
        Recording,
        Stopping
    }
    RecordingState _recordingState = RecordingState.Idle;
    bool _isManuallyTriggered = false;
    string _micDeviceName = null;
    int _recordBufLen = 10; // seconds
    int _firstSample = 0;
    int _lastSilenceSample = 0;
    int _audioFrequency = 0;
    int _audioChannel = 1;
    int _audioTotalSamples = 0;
    int _porcupineFrameLength = 0; // Porcupine frame length
    int _numKeywords = 0;
    bool _isPorcupineProcessing = false;
    float _recordingTime = 0;
    AudioClip _recording;

    void Start()
    {
        _capturePhoto = GetComponent<CapturePhoto>();
        _webSocketClient = GetComponent<WebSocketClient>();
        _userFeedback = GetComponent<UserFeedback>();
        _audioSource = GetComponent<AudioSource>();
        InitializeRecording();
    }

    void Update()
    {
        // Used for quick manual test
        bool isCurrentlyTriggered = false;
        
        if (Input.GetKeyDown(KeyCode.R))
        {
            Debug.Log("trying to capture audio");
            _capturePhoto.Capture();
            isCurrentlyTriggered = true;
        }
        if (_recordingState == RecordingState.Idle && isCurrentlyTriggered)
        {
            StartRecording();
            _isManuallyTriggered = true; // Used to prevent stopping the recording when initiated by voice activation
        }

        if (_isManuallyTriggered && (_recordingState == RecordingState.Recording) && (!isCurrentlyTriggered))
        {
            StopRecording();
            _isManuallyTriggered = false;
        }

        if (_recordingState == RecordingState.EarlyRecording)
        {
            _recordingTime += Time.deltaTime;
            if (_recordingTime > _minimalAudioDuration)
            {
                _recordingState = RecordingState.Recording;
                _recordingTime = 0;
            }
            // Flash the indicator
            _activeRecordingIndicator.SetActive(_recordingTime % (2 * _flashInterval) < _flashInterval); // Toggle active state
        }
        else if (_recordingState == RecordingState.Recording)
        {
            // Periodically check if the user is still talking, if not, stop the recording
            _recordingTime += Time.deltaTime;
            if (_recordingTime > _silenceCheckInterval)
            {
                CheckSilence();
                _recordingTime = 0;
            }
            // Flash the indicator
            _activeRecordingIndicator.SetActive(_recordingTime % (2 * _flashInterval) < _flashInterval); // Toggle active state
        }
        else
        {
            _activeRecordingIndicator.SetActive(false);
        }
    }

    #region Audio Input

    void InitializeRecording()
    {
        string platform = GetPlatform();
        var keywords = new List<string>
        {
            GetKeywordPaths(platform, "porcupine"),
            GetKeywordPaths(platform, "hey google"),
            GetKeywordPaths(platform, "alexa"),
            GetKeywordPaths(platform, "jarvis"),
        };
        // add one more keyword for registration on Android
        if (platform == "android")
        {
            keywords.Add(GetKeywordPaths(platform, "confirm"));
            keywords.Add(GetKeywordPaths(platform, "decline"));
            keywords.Add(GetKeywordPaths(platform, "registration"));
        }
        _numKeywords = keywords.Count;
        try
        {
            _porcupine = Porcupine.FromKeywordPaths(accessKey: Utils.PORCUPINE_ACCESS_KEY, keywordPaths: keywords);
        }
        catch (PorcupineInvalidArgumentException ex)
        {
            Debug.LogError(ex.Message);
        }
        catch (PorcupineActivationException)
        {
            Debug.LogError("AccessKey activation error");
        }
        catch (PorcupineActivationLimitException)
        {
            Debug.LogError("AccessKey reached its device limit");
        }
        catch (PorcupineActivationRefusedException)
        {
            Debug.LogError("AccessKey refused");
        }
        catch (PorcupineActivationThrottledException)
        {
            Debug.LogError("AccessKey has been throttled");
        }
        catch (PorcupineException ex)
        {
            Debug.LogError("PorcupineManager was unable to initialize: " + ex.Message);
        }

        // get the device name of the first available microphone
        foreach (var device in Microphone.devices)
        {
            Debug.Log("Audio Source Name: " + device);
            _micDeviceName = device;
            break;
        }

        // Get parameters from Porcupine if available, otherwise use default values
        if (_porcupine != null)
        {
            _porcupineFrameLength = _porcupine.FrameLength;
            _audioFrequency = _porcupine.SampleRate;
        }
        else
        {
            _audioFrequency = 44100; // Default value
            int minFreq;
            int maxFreq;
            Microphone.GetDeviceCaps(_micDeviceName, out minFreq, out maxFreq);
            if (maxFreq < _audioFrequency)
                _audioFrequency = maxFreq;
        }

        //Start the recording
        _recording = Microphone.Start(_micDeviceName, true, _recordBufLen, _audioFrequency);
        _audioChannel = _recording.channels;
        _audioTotalSamples = _recording.samples;
        if (_porcupine != null)
            StartCoroutine(RecordData());
        Debug.Log("Recording initialized.");
    }


    void StartRecording()
    {
        if (_recording == null)
        {
            Debug.Log("Recording is null, not start");
            return;
        }
        if (_recordingState != RecordingState.Idle)
        {
            Debug.Log("Already recording, first stop the previous one...");
            return;
        }
        _firstSample = Microphone.GetPosition(_micDeviceName);
        _lastSilenceSample = _firstSample;
        _recordingTime = 0;
        _recordingState = RecordingState.EarlyRecording;
    }

    async void StopRecording()
    {
        if (_recording == null)
        {
            Debug.Log("Recording is null, not end");
            return;
        }
        if (_recordingState == RecordingState.Idle)
        {
            Debug.Log("No active recording...");
            return;
        }
        if (_recordingState == RecordingState.Stopping)
        {
            Debug.Log("Currently stopping the recording...");
            return;
        }
        _recordingState = RecordingState.Stopping;
        //End the recording 
        int recordingEndSample = Microphone.GetPosition(_micDeviceName);
        var RecordSamples = (recordingEndSample - _firstSample + _recordBufLen * _recording.frequency) % (_recordBufLen * _recording.frequency);
        var RecordTime = (float)(RecordSamples) / _recording.frequency;

        //Trim the audioclip by the length of the recording
        AudioClip truncatedClip = TruncateClip(_recording, _firstSample, recordingEndSample);

        // Save the recording as a WAV file in the Application.persistentDataPath
        string filepath = await SaveWav("recorded_audio", truncatedClip);
        Debug.Log("Audio recording saved: " + filepath);

        // Send the audio file to the server
        _webSocketClient.SendAudioToServer(filepath);

        _recordingTime = 0;
        _recordingState = RecordingState.Idle;
    }

    void OnApplicationQuit()
    {
        if (_porcupine != null)
        {
            _isPorcupineProcessing = false;
            _porcupine.Dispose();
        }
    }
    #endregion

    #region Audio Processing
    private IEnumerator RecordData()
    {
        float[] sampleFrame = new float[_porcupineFrameLength];
        int startReadPos = 0;
        _isPorcupineProcessing = true;

        while (_isPorcupineProcessing)
        {
            int curClipPos = Microphone.GetPosition(_micDeviceName);
            if (curClipPos < startReadPos)
                curClipPos += _recording.samples;

            int samplesAvailable = curClipPos - startReadPos;
            if (samplesAvailable < _porcupineFrameLength)
            {
                yield return null;
                continue;
            }

            int endReadPos = startReadPos + _porcupineFrameLength;
            if (endReadPos > _recording.samples)
            {
                // fragmented read (wraps around to beginning of clip)
                // read bit at end of clip
                int numSamplesClipEnd = _recording.samples - startReadPos;
                float[] endClipSamples = new float[numSamplesClipEnd];
                _recording.GetData(endClipSamples, startReadPos);

                // read bit at start of clip
                int numSamplesClipStart = endReadPos - _recording.samples;
                float[] startClipSamples = new float[numSamplesClipStart];
                _recording.GetData(startClipSamples, 0);

                // combine to form full frame
                Buffer.BlockCopy(endClipSamples, 0, sampleFrame, 0, numSamplesClipEnd);
                Buffer.BlockCopy(startClipSamples, 0, sampleFrame, numSamplesClipEnd, numSamplesClipStart);
            }
            else
            {
                _recording.GetData(sampleFrame, startReadPos);
            }

            startReadPos = endReadPos % _recording.samples;

            // converts to 16-bit int samples
            short[] frame = new short[sampleFrame.Length];
            for (int i = 0; i < _porcupineFrameLength; i++)
            {
                frame[i] = (short)Math.Floor(sampleFrame[i] * short.MaxValue);
            }

            wakeWordCallback(_porcupine.Process(frame));
        }
    }

    void wakeWordCallback(int keywordIndex)
    {
        if ((keywordIndex == _webSocketClient.ClientID && _webSocketClient.ClientID < _numKeywords)
            || (keywordIndex == 0 && _webSocketClient.ClientID >= _numKeywords))
        {
            Debug.Log("Keyword recognized for audio recording, start audio recording");
            // Start audio recording, capture a photo and send to server as reference
            _capturePhoto.Capture();
            StartRecording();
        }
        else if (keywordIndex == _numKeywords - 3)
        {
            // confirm dialog
            _userFeedback.UploadConfirmedByUser();
        }
        else if (keywordIndex == _numKeywords - 2)
        {
            // decline dialog
            _userFeedback.CancelUploadByUser();
        }
        // Use the last keyword for registration
        else if (keywordIndex == _numKeywords - 1)
        {
            Debug.Log("Keyword recognized for registration, start registration process");
            _capturePhoto.Capture(true);
        }
    }

    async void CheckSilence()
    {
        // get starting time to calculate processing time
        float startTime = Time.realtimeSinceStartup;

        int recordingEndSample = Microphone.GetPosition(_micDeviceName);
        AudioClip truncatedClip = TruncateClip(_recording, _lastSilenceSample, recordingEndSample);
        float[] samples = new float[truncatedClip.samples * truncatedClip.channels];
        truncatedClip.GetData(samples, 0);
        AudioStatistics statistics = await CalculateAudioStatisticsAsync(samples);
        if (statistics.Max < _silenceThreshold)
        {
            Debug.Log("Silence detected, stopping recording...");
            StopRecording();
        }
        _lastSilenceSample = recordingEndSample;

        float endTime = Time.realtimeSinceStartup;
        //_webSocketClient.SendTextToServer("Check silence processing time: " + (endTime - startTime) + " seconds, RMS: " + statistics.RMS + ", Max: " + statistics.Max + ", Min: " + statistics.Min);
    }

    public class AudioStatistics
    {
        public float RMS { get; set; }
        public float Max { get; set; }
        public float Min { get; set; }
    }

    async Task<AudioStatistics> CalculateAudioStatisticsAsync(float[] samples)
    {
        return await Task.Run(() => CalculateAudioStatistics(samples));
    }

    AudioStatistics CalculateAudioStatistics(float[] samples)
    {
        float sum = 0;
        float max = float.MinValue;
        float min = float.MaxValue;
        foreach (var sample in samples)
        {
            sum += sample * sample;
            if (sample > max) max = sample;
            if (sample < min) min = sample;
        }
        float rms = Mathf.Sqrt(sum / samples.Length);

        return new AudioStatistics
        {
            RMS = rms,
            Max = max,
            Min = min
        };
    }

    private AudioClip TruncateClip(AudioClip originalClip, int startSample, int endSample)
    {
        int sampleCount = _audioFrequency * _recordBufLen; // Total samples in the buffer
        int lengthSamples = (endSample - startSample + sampleCount) % sampleCount;
        AudioClip newClip = AudioClip.Create("TruncatedClip", lengthSamples, _audioChannel, _audioFrequency, false);

        float[] data = new float[lengthSamples * _audioChannel];
        if (endSample > startSample)
        {
            originalClip.GetData(data, startSample);
        }
        else
        {
            int firstPartSampleCount = _audioTotalSamples - startSample; // From startSample to end of buffer
            int secondPartSampleCount = endSample; // From start of buffer to endSample

            float[] firstPart = new float[firstPartSampleCount * _audioChannel];
            float[] secondPart = new float[secondPartSampleCount * _audioChannel];
            _recording.GetData(firstPart, startSample);
            _recording.GetData(secondPart, 0);

            firstPart.CopyTo(data, 0);
            secondPart.CopyTo(data, firstPartSampleCount * _audioChannel);

        }
        newClip.SetData(data, 0);

        return newClip;
    }
    #endregion

    #region Save as WAV file
    async Task<string> SaveWav(string filename, AudioClip clip)
    {
        if (!filename.ToLower().EndsWith(".wav"))
        {
            filename += ".wav";
        }

        var filepath = Application.persistentDataPath + "/" + filename;

        int HEADER_SIZE = 44;

        // Convert and write audio data
        var audioData = new float[clip.samples * clip.channels];
        clip.GetData(audioData, 0);
        var hz = clip.frequency;
        var channels = clip.channels;
        var samples = clip.samples;

        await Task.Run(() =>
        {
            using (var fileStream = new FileStream(filepath, FileMode.Create))
            {
                // Create header
                byte[] header = new byte[HEADER_SIZE];
                fileStream.Write(header, 0, header.Length);

                // Convert and write audio data
                var bytesData = ConvertAndWrite(fileStream, audioData);

                // Write header again with updated data
                WriteHeader(fileStream, hz, channels, samples, header, bytesData);
            }
        });

        return filepath; // TODO: return false if there's a failure
    }

    static int ConvertAndWrite(FileStream fileStream, float[] audioData)
    {
        var bytesData = new byte[audioData.Length * 2];
        var rescaleFactor = 32767;
        for (int i = 0; i < audioData.Length; i++)
        {
            short value = (short)(audioData[i] * rescaleFactor);
            BitConverter.GetBytes(value).CopyTo(bytesData, i * 2);
        }

        fileStream.Write(bytesData, 0, bytesData.Length);
        return bytesData.Length;
    }

    static void WriteHeader(FileStream fileStream, int hz, int channels, int samples, byte[] header, int bytesDataLength)
    {
        fileStream.Seek(0, SeekOrigin.Begin);

        byte[] riff = System.Text.Encoding.UTF8.GetBytes("RIFF");
        riff.CopyTo(header, 0);
        BitConverter.GetBytes(header.Length + bytesDataLength - 8).CopyTo(header, 4);

        byte[] wave = System.Text.Encoding.UTF8.GetBytes("WAVE");
        wave.CopyTo(header, 8);

        byte[] fmt = System.Text.Encoding.UTF8.GetBytes("fmt ");
        fmt.CopyTo(header, 12);
        BitConverter.GetBytes(16).CopyTo(header, 16); // sub chunk size
        BitConverter.GetBytes((ushort)1).CopyTo(header, 20); // PCM
        BitConverter.GetBytes((ushort)channels).CopyTo(header, 22);
        BitConverter.GetBytes(hz).CopyTo(header, 24);
        BitConverter.GetBytes(hz * channels * 2).CopyTo(header, 28); // byte rate
        BitConverter.GetBytes((ushort)(channels * 2)).CopyTo(header, 32); // block align
        BitConverter.GetBytes((ushort)16).CopyTo(header, 34); // bits per sample

        byte[] data = System.Text.Encoding.UTF8.GetBytes("data");
        data.CopyTo(header, 36);
        BitConverter.GetBytes(samples * channels * 2).CopyTo(header, 40); // sub chunk 2 size

        fileStream.Write(header, 0, header.Length);
    }
    #endregion



    #region Audio Output
    public IEnumerator PlayAudioClipFromFile(string filePath, AudioType type)
    {
        string url = "file://" + filePath;
        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(url, type))
        {
            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.ConnectionError || www.result == UnityWebRequest.Result.ProtocolError)
            {
                Debug.LogError(www.error);
            }
            else
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                if (clip != null)
                {
                    _audioSource.clip = clip;
                    _audioSource.Play();
                }
            }
        }
    }
    #endregion

    #region Porcupine keyword path handling
    private static string GetKeywordPaths(string platform, string keyword)
    {

#if !UNITY_EDITOR && UNITY_ANDROID

            string keywordFilesDir = Path.Combine(Path.Combine(Application.persistentDataPath, "keyword_files"), platform);
            if (!Directory.Exists(keywordFilesDir))
            {
                Directory.CreateDirectory(keywordFilesDir);
            }

            string assetDir = Path.Combine(Path.Combine(Application.streamingAssetsPath, "keyword_files"), platform);
            ExtractResource(Path.Combine(
                assetDir,
                string.Format("{0}_{1}.ppn", keyword.Replace("_", " ").ToLower(), platform)));        

#else

        string keywordFilesDir = Path.Combine(Application.streamingAssetsPath, "keyword_files", platform);

#endif

        return Path.Combine(keywordFilesDir, keyword + "_" + platform + ".ppn");
    }

#if !UNITY_EDITOR && UNITY_ANDROID

        public static string ExtractResource(string filePath)
        {
            if (!filePath.StartsWith(Application.streamingAssetsPath))
            {
                throw new PorcupineIOException($"File '{filePath}' not found in streaming assets path.");
            }

            string dstPath = filePath.Replace(Application.streamingAssetsPath, Application.persistentDataPath);
            string dstDir = Path.GetDirectoryName(dstPath);
            if (!Directory.Exists(dstDir))
            {
                Directory.CreateDirectory(dstDir);
            }

            var loadingRequest = UnityWebRequest.Get(filePath);
            loadingRequest.SendWebRequest();

            while (!loadingRequest.isDone)
            {
                if (loadingRequest.isNetworkError || loadingRequest.isHttpError)
                {
                    break;
                }
            }
            if (!(loadingRequest.isNetworkError || loadingRequest.isHttpError))
            {
                File.WriteAllBytes(dstPath, loadingRequest.downloadHandler.data);
            }

            return dstPath;
        }
#endif

    private static string GetPlatform()
    {
        switch (Application.platform)
        {
            case RuntimePlatform.WindowsEditor:
            case RuntimePlatform.WindowsPlayer:
                return "windows";
            case RuntimePlatform.OSXEditor:
            case RuntimePlatform.OSXPlayer:
                return "mac";
            case RuntimePlatform.LinuxEditor:
            case RuntimePlatform.LinuxPlayer:
                return "linux";
            case RuntimePlatform.IPhonePlayer:
                return "ios";
            case RuntimePlatform.Android:
                return "android";
            default:
                throw new Exception(string.Format("Platform '{0}' not supported", Application.platform));
        }
    }
    #endregion
}
