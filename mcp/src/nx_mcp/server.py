# Publication copy modified by repository maintainer on 2026-11-10.
# Original authors and licenses: docs/ATTRIBUTION.md.
"""MCP server entry point — stdio transport."""

from __future__ import annotations

import asyncio
import logging
import os
import sys
from pathlib import Path

from mcp.server.mcpserver import MCPServer

from nx_mcp.bridge import DescriptorBridgeClient
from nx_mcp.certified import BridgeCaller, create_certified_server
from nx_mcp.workspace import Workspace

logger = logging.getLogger("nx_mcp")


def _select_backend() -> BridgeCaller:
    """Prefer the resident C# Loader (V2) when ready; else fall back to the
    Python bridge. Tool names and parameter formats are unchanged.

    Override with NX_MCP_BACKEND=python|loader|auto (default auto).
    """
    forced = os.environ.get("NX_MCP_BACKEND", "auto")
    if forced == "python":
        logger.info("NX MCP: backend forced to Python bridge")
        return DescriptorBridgeClient()
    if forced == "loader":
        from nx_mcp.loader_bridge import LoaderBridge

        logger.info("NX MCP: backend forced to C# Loader")
        return LoaderBridge()
    try:
        from nx_mcp.loader_bridge import LoaderBridge

        loader = LoaderBridge()
        if loader.ping():
            logger.info("NX MCP: using C# Loader backend (resident, no Alt+F8)")
            return loader
        logger.info("NX MCP: Loader pipe not ready; falling back to Python bridge")
    except Exception as exc:  # pragma: no cover - defensive
        logger.info("NX MCP: Loader backend unavailable (%s); falling back to Python bridge", exc)
    return DescriptorBridgeClient()


def create_server(
    bridge: BridgeCaller | None = None,
    workspace: Workspace | None = None,
    *,
    enable_experimental: bool | None = None,
    enable_journal: bool | None = None,
) -> MCPServer:
    """Create the certified v0.2 MCP server."""
    if workspace is None and (workspace_root := os.environ.get("NX_MCP_WORKSPACE")):
        workspace = Workspace(Path(workspace_root))
    if enable_experimental is None:
        enable_experimental = os.environ.get("NX_MCP_ENABLE_EXPERIMENTAL") == "1"
    if enable_journal is None:
        enable_journal = os.environ.get("NX_MCP_ENABLE_JOURNAL") == "1"
    return create_certified_server(
        bridge or _select_backend(),
        workspace,
        enable_experimental=enable_experimental,
        enable_journal=enable_experimental and enable_journal,
    )


async def async_main() -> None:
    """Run the MCP server with stdio transport."""
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s [%(name)s] %(levelname)s: %(message)s",
        stream=sys.stderr,
    )

    server = create_server()
    logger.info("NX MCP Server starting (stdio transport)")
    await server.run_stdio_async()


def main() -> None:
    """Entry point for the nx-mcp console script."""
    asyncio.run(async_main())


if __name__ == "__main__":
    main()
