using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BrainService.Services.Audio;

public class SileroVadNode : IDisposable
{
    private readonly InferenceSession _session;
    
    // Silero VAD state - flattened shape [2, 1, 128] -> 256 items.
    private float[] _state = null!;
    private float[][] _context = null!;
    private int _lastSr;
    private int _lastBatchSize;
    
    // ReSharper disable InconsistentNaming
    private const int SampleRate16k = 16000;
    private const int WindowSize16k = 512;
    private const int ContextSize16k = 64;
    // ReSharper restore InconsistentNaming
    
    public SileroVadNode(InferenceSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ResetStates();
    }
    
    public void ResetStates()
    {
        _state = new float[256];
        _context = [];
        _lastSr = 0;
        _lastBatchSize = 0;
    }

    // ReSharper disable once InconsistentNaming
    public float GetSpeechProbability(ReadOnlySpan<float> audio16kHz)
    {
        if (audio16kHz.Length != WindowSize16k)
        {
            throw new ArgumentException($"Expected exactly {WindowSize16k} samples, got {audio16kHz.Length}");
        }
        
        const int batchSize = 1;
        
        // Reset states if needed
        if (_lastBatchSize == 0 || (_lastSr != 0 && _lastSr != SampleRate16k) || (_lastBatchSize != 0 && _lastBatchSize != batchSize))
        {
            ResetStates();
        }
        
        // Initialize context if needed
        if (_context.Length == 0)
        {
            _context = new float[batchSize][];
            for (int i = 0; i < batchSize; i++)
            {
                _context[i] = new float[ContextSize16k];
            }
        }
        
        // Concatenate context with input
        var inputWithContext = new float[ContextSize16k + WindowSize16k];
        _context[0].CopyTo(inputWithContext, 0);
        audio16kHz.CopyTo(inputWithContext.AsSpan(ContextSize16k));
        
        var inputTensor = new DenseTensor<float>(inputWithContext, new[] { 1, inputWithContext.Length });
        var srTensor = new DenseTensor<long>(new[] { (long)SampleRate16k }, new[] { 1 });
        var stateTensor = new DenseTensor<float>(_state, new[] { 2, 1, 128 });
        
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor),
            NamedOnnxValue.CreateFromTensor("sr", srTensor),
            NamedOnnxValue.CreateFromTensor("state", stateTensor)
        };
        
        using var results = _session.Run(inputs);
        
        // Extract outputs
        var output = results.First(o => o.Name == "output").AsTensor<float>();
        var newState = results.First(o => o.Name == "stateN").AsTensor<float>();
        
        // Update context - save last ContextSize16k samples
        Array.Copy(inputWithContext, inputWithContext.Length - ContextSize16k, _context[0], 0, ContextSize16k);
        
        if (newState is DenseTensor<float> denseState)
        {
            denseState.Buffer.Span.CopyTo(_state);
        }
        else
        {
            // Fallback just in case ML.ONNX changes underlying tensor types
            var idx = 0;
            foreach (var val in newState)
            {
                _state[idx++] = val;
            }
        }
        
        _lastSr = SampleRate16k;
        _lastBatchSize = batchSize;
        
        return output[0];
    }
    
    public void Dispose()
    {
        _session.Dispose();
    }
}
