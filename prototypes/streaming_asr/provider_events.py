from __future__ import annotations

from dataclasses import dataclass
from typing import Literal, TypeAlias


ProviderEventKind = Literal["partial", "stable", "error", "connection_state"]
ConnectionStateValue = Literal[
    "connecting",
    "connected",
    "task_started",
    "task_finished",
    "closed",
    "reconnecting",
]


@dataclass(frozen=True)
class PartialTranscript:
    text: str
    raw_type: str
    timestamp: float
    kind: ProviderEventKind = "partial"


@dataclass(frozen=True)
class StableTranscript:
    text: str
    raw_type: str
    timestamp: float
    kind: ProviderEventKind = "stable"


@dataclass(frozen=True)
class AsrError:
    message: str
    raw_type: str
    timestamp: float
    kind: ProviderEventKind = "error"


@dataclass(frozen=True)
class ConnectionState:
    state: ConnectionStateValue | str
    raw_type: str
    timestamp: float
    kind: ProviderEventKind = "connection_state"


ProviderEvent: TypeAlias = PartialTranscript | StableTranscript | AsrError | ConnectionState
