from dependency_injector import containers, providers

from config import Settings
from infra.resources import init_grpc_channel, init_valkey_client

from generated import brain_pb2_grpc
from cogs.util import UtilCog
from cogs.state_manager import StateManager
from cogs.voice import VoiceCog
from services.state import StateService
from services.voice import VoiceService
from services.util import UtilService

class Container(containers.DeclarativeContainer):
    config = providers.Configuration(pydantic_settings=[Settings()]) # type: ignore

    node_id = providers.Object(None)

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

    state_service = providers.Factory(
        StateService,
        valkey=valkey_client,
        node_id=node_id
    )

    util_service = providers.Factory(
        UtilService,
        brain=brain_stub
    )

    voice_service = providers.Factory(
        VoiceService,
        brain=brain_stub,
    )

    util_cog = providers.Factory(
        UtilCog,
        util_service=util_service
    )

    state_cog = providers.Factory(
        StateManager,
        state_service=state_service
    )

    voice_cog = providers.Factory(
        VoiceCog,
        voice_service=voice_service,
        node_id=node_id
    )
