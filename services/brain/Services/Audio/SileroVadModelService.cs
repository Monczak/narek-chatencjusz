using Microsoft.ML.OnnxRuntime;
using SessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace BrainService.Services.Audio;

public class SileroVadModelService
{
    private readonly string _modelPath;
    private readonly string _modelUrl;
    private readonly ILogger<SileroVadModelService> _logger;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;
    
    public SileroVadModelService(
        IConfiguration configuration,
        ILogger<SileroVadModelService> logger, 
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.Timeout = TimeSpan.FromMinutes(5); // Generous timeout for iffy connections
        
        _modelUrl = configuration.GetValue<string>("Audio:VadModelUrl") 
            ?? "https://raw.githubusercontent.com/snakers4/silero-vad/master/src/silero_vad/data/silero_vad.onnx";
        
        var modelDir = Path.Combine(AppContext.BaseDirectory, "models");
        _modelPath = Path.Combine(modelDir, "silero_vad.onnx");
    }
    
    public async Task<string> EnsureModelAsync(CancellationToken ct = default)
    {
        if (_initialized && File.Exists(_modelPath))
        {
            return _modelPath;
        }
        
        await _initLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_initialized && File.Exists(_modelPath))
            {
                return _modelPath;
            }
            
            if (File.Exists(_modelPath))
            {
                _logger.LogInformation("Silero VAD model already exists at {Path}", _modelPath);
                _initialized = true;
                return _modelPath;
            }
            
            _logger.LogInformation("Downloading Silero VAD model from {Url}...", _modelUrl);
            Directory.CreateDirectory(Path.GetDirectoryName(_modelPath)!);
            
            using var response = await _httpClient.GetAsync(_modelUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            
            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            _logger.LogInformation("Model size: {Size:N0} bytes", totalBytes);
            
            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(_modelPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
            
            await contentStream.CopyToAsync(fileStream, ct);
            
            _logger.LogInformation("Silero VAD model downloaded successfully to {Path}", _modelPath);
            _initialized = true;
            return _modelPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download Silero VAD model");
            
            // Clean up partial download
            if (File.Exists(_modelPath))
            {
                try
                {
                    File.Delete(_modelPath);
                }
                catch
                {
                    // Ignore exceptions
                }
            }
            
            throw;
        }
        finally
        {
            _initLock.Release();
        }
    }
    
    public InferenceSession CreateSession()
    {
        if (!_initialized || !File.Exists(_modelPath))
        {
            throw new InvalidOperationException("Model not initialized. Call EnsureModelAsync first.");
        }
        
        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = 1,
            EnableCpuMemArena = true
        };
        
        return new InferenceSession(_modelPath, sessionOptions);
    }
}
