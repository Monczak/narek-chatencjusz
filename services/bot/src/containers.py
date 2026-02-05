from dependency_injector import containers, providers

from config import Settings
from infra.resources import init_grpc_channel, init_valkey_client

from generated import brain_pb2_grpc
from cogs.util import UtilCog
from cogs.state_manager import StateManager
from cogs.voice import VoiceCog
from services.util import UtilService

class Container(containers.DeclarativeContainer):
    config = providers.Configuration(pydantic_settings=[Settings()]) # type: ignore

    brain_grpc_channel = providers.Resource(
        init_grpc_channel,
        url=config.brain_url
    )

    brain_stub = providers.Factory(
        brain_pb2_grpc.BrainStub,
        channel=brain_grpc_channel
    )

    valkey_client = providers.Resource(
        init_valkey_client, 
        url=config.valkey_url
    )

    util_service = providers.Factory(
        UtilService,
        brain=brain_stub
    )

    util_cog = providers.Factory(
        UtilCog,
        util_service=util_service
    )

    state_cog = providers.Factory(
        StateManager,
        valkey_client=valkey_client
    )

    voice_cog = providers.Factory(
        VoiceCog,
        brain_stub=brain_stub
    )
