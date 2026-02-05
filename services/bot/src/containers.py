from dependency_injector import containers, providers

from config import Settings
from infra.brain import BrainClient
from infra.resources import init_grpc_channel
from infra.valkey import ValkeyClient

class Container(containers.DeclarativeContainer):
    config = providers.Configuration(pydantic_settings=[Settings()]) # type: ignore

    grpc_channel = providers.Resource(
        init_grpc_channel,
        url=config.brain_url
    )

    brain_client = providers.Factory(
        BrainClient,
        channel=grpc_channel
    )

    valkey_client = providers.Resource(
        ValkeyClient, 
        url=config.valkey_url
    )
