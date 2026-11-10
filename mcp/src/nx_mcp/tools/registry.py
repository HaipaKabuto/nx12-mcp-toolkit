# Publication copy modified by repository maintainer on 2026-11-10.
# Original authors and licenses: docs/ATTRIBUTION.md.
"""Tool registration decorator and registry."""

from __future__ import annotations

from collections.abc import Callable, Coroutine
from typing import Any


class _ToolDef:
    """Internal representation of a registered tool."""

    def __init__(
        self,
        name: str,
        description: str,
        params: dict[str, dict[str, Any]],
        handler: Callable[..., Coroutine],
    ) -> None:
        self.name = name
        self.description = description
        self.params = params
        self.handler = handler

    def to_mcp_tool(self) -> dict[str, Any]:
        """Convert to MCP Tool dict format."""
        properties: dict[str, Any] = {}
        required: list[str] = []
        for pname, pdef in self.params.items():
            properties[pname] = {
                "type": pdef.get("type", "string"),
                "description": pdef.get("description", ""),
            }
            if pdef.get("required", True):
                required.append(pname)

        schema: dict[str, Any] = {
            "type": "object",
            "properties": properties,
        }
        if required:
            schema["required"] = required

        return {
            "name": self.name,
            "description": self.description,
            "inputSchema": schema,
        }


class ToolRegistry:
    """Global registry for MCP tools."""

    _tools: dict[str, _ToolDef] = {}

    @classmethod
    def register(cls, tool_def: _ToolDef) -> None:
        cls._tools[tool_def.name] = tool_def

    @classmethod
    def list_tools(cls) -> list[dict[str, Any]]:
        return [t.to_mcp_tool() for t in cls._tools.values()]

    @classmethod
    def get_handler(cls, name: str) -> Callable | None:
        tool = cls._tools.get(name)
        return tool.handler if tool else None

    @classmethod
    def get_tool_names(cls) -> list[str]:
        return list(cls._tools.keys())

    @classmethod
    def clear(cls) -> None:
        cls._tools.clear()


def mcp_tool(
    name: str,
    description: str,
    params: dict[str, dict[str, Any]],
) -> Callable:
    """Decorator to register an async function as an MCP tool."""

    def decorator(func: Callable) -> Callable:
        tool_def = _ToolDef(
            name=name,
            description=description,
            params=params,
            handler=func,
        )
        ToolRegistry.register(tool_def)
        return func

    return decorator
