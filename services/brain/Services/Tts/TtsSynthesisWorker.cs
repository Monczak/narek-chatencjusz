using System.Text;
using System.Threading.Channels;
using BrainService.Domain.Tts;
using BrainService.Services.Audio.Nodes;

namespace BrainService.Services.Tts;

public class TtsSynthesisWorker(
    TtsNode ttsNode,
    SoundboardNode soundboard,
    ITtsProvider provider,
    TtsVoiceDefinition voice,
    int soundboardDrainBufferMs,
    ILogger logger)
{
    private readonly Channel<string> _sentences = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly StringBuilder _enqueuedText = new();
    private bool _firstFrame = true;
    
    public string EnqueuedText => _enqueuedText.ToString().Trim();

    public void EnqueueSentence(string sentence)
    {
        _sentences.Writer.TryWrite(sentence);
    }

    public void Complete()
    {
        _sentences.Writer.TryComplete();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var sentence in _sentences.Reader.ReadAllAsync(ct))
            {
                if (string.IsNullOrWhiteSpace(sentence)) continue;

                TtsAudioResult result;
                try
                {
                    result = await provider.SynthesizeAsync(sentence, voice, ct);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[TTS] Synthesis failed for sentence: {Text}", sentence);
                    continue;
                }

                if (_firstFrame)
                {
                    _firstFrame = false;
                    await WaitForSoundboardAsync(ct);
                }

                ttsNode.Enqueue(result.Samples);

                if (_enqueuedText.Length > 0) _enqueuedText.Append(' ');
                _enqueuedText.Append(sentence);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
    }

    private async Task WaitForSoundboardAsync(CancellationToken ct)
    {
        const int pollMs = 20;

        while (!ct.IsCancellationRequested)
        {
            // Each frame is 1920 stereo float samples = 20 ms at 48 kHz stereo
            var remainingMs = soundboard.QueueDepth * 20;
            if (remainingMs <= soundboardDrainBufferMs) return;
            await Task.Delay(pollMs, ct);
        }
    }
}
