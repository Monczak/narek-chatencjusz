from dependency_injector import containers, providers

from config import Settings
from infra.resources import init_grpc_channel, init_valkey_client

from generated import brain_pb2_grpc
from bot import NarekChatencjuszBot
from cogs.util import UtilCog
from cogs.state_manager import StateManager
from cogs.voice import VoiceCog
from services.interaction import InteractionService
from services.response import ResponseService
from services.messaging import CommandListener
from services.state import StateService
from services.voice import VoiceService
from services.util import UtilService

class Container(containers.DeclarativeContainer):
    config = providers.Configuration(pydantic_settings=[Settings()]) # type: ignore

    node_id = providers.Object(None)
    version = providers.Object("0.0.0")

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
        url=config.valkey_url,
        decode_responses=True
    )

    valkey_binary_client = providers.Resource(
        init_valkey_client,
        url=config.valkey_url,
        decode_responses=False
    )

    state_service = providers.Factory(
        StateService,
        valkey=valkey_client,
        brain_stub=brain_stub,
        node_id=node_id,
    )

    util_service = providers.Factory(
        UtilService,
        brain=brain_stub
    )

    interaction_service = providers.Singleton(
        InteractionService
    )

    response_service = providers.Factory(
        ResponseService,
        interaction_service=interaction_service
    )

    voice_service = providers.Singleton(
        VoiceService,
        brain_stub=brain_stub,
        response_service=response_service,
        interaction_service=interaction_service
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
        response_service=response_service,
        interaction_service=interaction_service,
        node_id=node_id
    )

    command_listener = providers.Singleton(
        CommandListener,
        valkey_client=valkey_binary_client,
        node_id=node_id,
        voice_service=voice_service
    )

    bot = providers.Singleton(
        NarekChatencjuszBot,
        node_id=node_id,
        version=version,
        valkey_client=valkey_client,
        debug_guild_ids=config.debug_guild_ids,
        util_cog_factory = util_cog.provider,
        state_cog_factory = state_cog.provider,
        voice_cog_factory = voice_cog.provider,
        voice_service = voice_service,
        command_listener = command_listener
    )
