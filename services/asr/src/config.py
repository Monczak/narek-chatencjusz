from pydantic_settings import BaseSettings

class Settings(BaseSettings):
    model: str = "large-v3-turbo"
    device: str = "cuda"
    compute_type: str = "float16"
    flash_attention: bool = True

    num_workers: int = 1
    beam_size: int = 5

    grpc_port: int = 6060
    min_utterance_seconds: float = 0.5

    class Config:
        env_file = ".env"
        env_prefix = "ASR_"
