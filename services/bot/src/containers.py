from dependency_injector import containers, providers

from config import Settings

from infra.brain import BrainClient
from infra.resources import init_grpc_channel, init_valkey_client

from services.util import UtilService

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
        init_valkey_client, 
        url=config.valkey_url
    )

    util_service = providers.Factory(
        UtilService,
        brain=brain_client
    )
