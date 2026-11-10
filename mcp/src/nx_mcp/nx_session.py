# Publication copy modified by repository maintainer on 2026-11-10.
# Original authors and licenses: docs/ATTRIBUTION.md.
"""NX Open session wrapper — singleton that manages the NX connection."""

from __future__ import annotations

import logging
from typing import Any

logger = logging.getLogger("nx_mcp")


class NXSession:
    """Singleton wrapper around NXOpen.Session.

    Connects to a running NX instance. If NX is not available,
    tools will return descriptive errors instead of crashing.
    """

    _instance: NXSession | None = None

    def __init__(self) -> None:
        self._session: Any = None
        self._uf_session: Any = None
        self._connected = False
        self._connect()

    def _connect(self) -> None:
        """Attempt to connect to the NX session."""
        try:
            import NXOpen

            self._session = NXOpen.Session.GetSession()
            self._connected = True
            logger.info("Connected to NX session")
        except Exception as e:
            self._connected = False
            self._session = None
            logger.warning("Could not connect to NX: %s", e)

    @classmethod
    def get_instance(cls) -> NXSession:
        """Get or create the singleton session."""
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    @classmethod
    def reset(cls) -> None:
        """Reset the singleton — used in tests."""
        cls._instance = None

    @property
    def is_connected(self) -> bool:
        """Whether we have a live NX connection."""
        return self._connected and self._session is not None

    @property
    def session(self) -> Any:
        """The raw NXOpen.Session object."""
        return self._session

    @property
    def work_part(self) -> Any | None:
        """The current work part, or None if not available."""
        if not self.is_connected:
            return None
        try:
            return self._session.Parts.Work
        except Exception:
            return None

    @property
    def uf_session(self) -> Any | None:
        """The NXOpen.UF.UFSession, lazily initialized."""
        if not self.is_connected:
            return None
        if self._uf_session is None:
            try:
                import NXOpen.UF

                self._uf_session = NXOpen.UF.UFSession.GetUFSession()
            except Exception:
                pass
        return self._uf_session

    def require(self) -> Any:
        """Return the session or raise a clear error if not connected."""
        if not self.is_connected:
            raise RuntimeError(
                "NX is not connected. Start NX and ensure UGII_BASE_DIR is set correctly."
            )
        return self._session

    def require_work_part(self) -> Any:
        """Return the work part or raise a clear error."""
        part = self.work_part
        if part is None:
            raise RuntimeError("No work part is open. Use nx_open_part or nx_create_part first.")
        return part
