# Publication copy modified by repository maintainer on 2026-11-10.
# Original authors and licenses: docs/ATTRIBUTION.md.
"""Standardized response types for MCP tools."""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from typing import Any


def _nx_exception_code(exc: Exception) -> str:
    """Extract error code from NX exceptions."""
    exc_type = type(exc).__name__
    if "NX" in exc_type:
        return f"NX_{exc_type.upper()}"
    msg = str(exc).lower()
    if "not found" in msg:
        return "NX_NOT_FOUND"
    if "permission" in msg or "access" in msg:
        return "NX_PERMISSION_DENIED"
    if "invalid" in msg:
        return "NX_INVALID_PARAMS"
    return "NX_INTERNAL_ERROR"


@dataclass
class ToolError:
    """Error response from an MCP tool."""

    status: str = "error"
    error_code: str = "NX_ERROR"
    message: str = ""
    suggestion: str | None = None

    def to_text(self) -> str:
        output: dict[str, Any] = {
            "status": self.status,
            "error_code": self.error_code,
            "message": self.message,
        }
        if self.suggestion:
            output["suggestion"] = self.suggestion
        return json.dumps(output, indent=2, ensure_ascii=False)


@dataclass
class ToolResult:
    """Success response from an MCP tool."""

    status: str = "success"
    data: dict[str, Any] = field(default_factory=dict)
    message: str = ""

    @classmethod
    def success(cls, data: dict[str, Any] | None = None, message: str = "") -> ToolResult:
        return cls(status="success", data=data or {}, message=message)

    @staticmethod
    def from_exception(
        exc: Exception,
        error_code: str | None = None,
        suggestion: str | None = None,
    ) -> ToolError:
        """Create an error response from an exception."""
        return ToolError(
            error_code=error_code or _nx_exception_code(exc),
            message=str(exc),
            suggestion=suggestion,
        )

    def to_text(self) -> str:
        output: dict[str, Any] = {"status": self.status, "message": self.message}
        if self.data:
            output["data"] = self.data
        return json.dumps(output, indent=2, ensure_ascii=False)
