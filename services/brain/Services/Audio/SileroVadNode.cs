using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BrainService.Services.Audio;

public class SileroVadNode : IDisposable
{
    private readonly InferenceSession _session;
    
    // Silero VAD state - shape [2, 1, 128]
    private float[][][] _state;
    private float[][] _context;
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
        _state = new float[2][][];
        _state[0] = new float[1][];
        _state[1] = new float[1][];
        _state[0][0] = new float[128];
        _state[1][0] = new float[128];
        _context = Array.Empty<float[]>();
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
        if (_lastBatchSize == 0)
        {
            ResetStates();
        }
        if (_lastSr != 0 && _lastSr != SampleRate16k)
        {
            ResetStates();
        }
        if (_lastBatchSize != 0 && _lastBatchSize != batchSize)
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
        
        // Prepare tensors
        var inputTensor = new DenseTensor<float>(inputWithContext, new[] { 1, inputWithContext.Length });
        var srTensor = new DenseTensor<long>(new[] { (long)SampleRate16k }, new[] { 1 });
        
        // Flatten state to 1D array for tensor
        var stateFlat = new float[_state.Length * _state[0].Length * _state[0][0].Length];
        var idx = 0;
        for (var i = 0; i < _state.Length; i++)
        {
            for (var j = 0; j < _state[i].Length; j++)
            {
                for (var k = 0; k < _state[i][j].Length; k++)
                {
                    stateFlat[idx++] = _state[i][j][k];
                }
            }
        }
        
        var stateTensor = new DenseTensor<float>(stateFlat, new[] { _state.Length, _state[0].Length, _state[0][0].Length });
        
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
        
        // Update state
        _state = new float[newState.Dimensions[0]][][];
        for (int i = 0; i < newState.Dimensions[0]; i++)
        {
            _state[i] = new float[newState.Dimensions[1]][];
            for (int j = 0; j < newState.Dimensions[1]; j++)
            {
                _state[i][j] = new float[newState.Dimensions[2]];
                for (int k = 0; k < newState.Dimensions[2]; k++)
                {
                    _state[i][j][k] = newState[i, j, k];
                }
            }
        }
        
        _lastSr = SampleRate16k;
        _lastBatchSize = batchSize;
        
        return output[0];
    }
    
    public void Dispose()
    {
        _session?.Dispose();
    }
}
