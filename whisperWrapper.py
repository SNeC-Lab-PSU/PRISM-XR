import whisper

WHISPER_MODEL = whisper.load_model("turbo")


def request_transcription_local(audio_file_path):
    result = WHISPER_MODEL.transcribe(audio_file_path)
    no_speech_flag = True
    for segment in result["segments"]:
        if segment["no_speech_prob"] < 0.5:
            no_speech_flag = False
            break
    if no_speech_flag:
        return None
    return result["text"]


if __name__ == "__main__":
    import time

    test_file_path = "temp/recorded_audio.wav"
    start_time = time.time()
    result = request_transcription_local(test_file_path)
    end_time = time.time()
    print(result)
    print(f"Time taken: {end_time - start_time} seconds")
