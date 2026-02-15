from pydantic import computed_field
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

    languages: str = ""

    class Config:
        env_file = ".env"
        env_prefix = "ASR_"

    @computed_field
    @property
    def allowed_languages(self) -> frozenset[str]:
        if not self.languages.strip():
            return frozenset()
        return frozenset(code.strip().lower() for code in self.languages.split(",") if code.strip())
