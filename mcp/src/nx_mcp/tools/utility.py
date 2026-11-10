# Publication copy modified by repository maintainer on 2026-11-10.
# Original authors and licenses: docs/ATTRIBUTION.md.
"""Utility & view tools — fit view, set orientation, undo, screenshot,
run journal, record start/stop."""

from __future__ import annotations

import os

from nx_mcp.nx_session import NXSession
from nx_mcp.response import ToolError, ToolResult
from nx_mcp.tools.registry import mcp_tool

# ---------------------------------------------------------------------------
# Orientation map for nx_set_view
# ---------------------------------------------------------------------------
_ORIENTATION_MAP: dict[str, str] = {
    "front": "kFront",
    "back": "kBack",
    "top": "kTop",
    "bottom": "kBottom",
    "left": "kLeft",
    "right": "kRight",
    "isometric": "kIsometric",
    "trimetric": "kTrimetric",
}


# ---------------------------------------------------------------------------
# 1. nx_fit_view
# ---------------------------------------------------------------------------
@mcp_tool(
    name="nx_fit_view",
    description="Fit all visible objects in the graphics viewport.",
    params={},
)
async def nx_fit_view() -> ToolResult | ToolError:
    """Fit the view so that all objects are visible."""
    try:
        work_part = NXSession.get_instance().require_work_part()

        work_part.ModelingViews.WorkView.Fit()

        return ToolResult.success(
            data={},
            message="View fitted — all objects visible.",
        )

    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 2. nx_set_view
# ---------------------------------------------------------------------------
@mcp_tool(
    name="nx_set_view",
    description="Set the graphics view orientation (front, back, top, bottom, left, right, isometric, trimetric).",
    params={
        "orientation": {
            "type": "string",
            "description": "View orientation: front, back, top, bottom, left, right, isometric, or trimetric.",
            "required": True,
        },
    },
)
async def nx_set_view(orientation: str) -> ToolResult | ToolError:
    """Set the view to a named orientation."""
    try:
        import NXOpen

        work_part = NXSession.get_instance().require_work_part()

        key = orientation.strip().lower()
        if key not in _ORIENTATION_MAP:
            valid = ", ".join(_ORIENTATION_MAP.keys())
            return ToolError(
                error_code="NX_INVALID_PARAMS",
                message=f"Invalid orientation '{orientation}'.",
                suggestion=f"Use one of: {valid}.",
            )

        nx_orient = getattr(NXOpen.View.ViewOrientation, _ORIENTATION_MAP[key])
        work_part.ModelingViews.WorkView.Orient(nx_orient)

        return ToolResult.success(
            data={"orientation": key},
            message=f"View set to {key}.",
        )

    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 3. nx_undo
# ---------------------------------------------------------------------------
@mcp_tool(
    name="nx_undo",
    description="Undo the last visible operation.",
    params={},
)
async def nx_undo() -> ToolResult | ToolError:
    """Undo the last visible mark."""
    try:
        session = NXSession.get_instance().require()

        session.UndoLastNVisibleMarks(1)

        return ToolResult.success(
            data={},
            message="Undo successful.",
        )

    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 4. nx_screenshot
# ---------------------------------------------------------------------------
@mcp_tool(
    name="nx_screenshot",
    description="Capture the current graphics viewport as a PNG image.",
    params={
        "path": {
            "type": "string",
            "description": "Full file path for the output PNG image.",
            "required": True,
        },
    },
)
async def nx_screenshot(path: str) -> ToolResult | ToolError:
    """Export the viewport as a PNG screenshot."""
    try:
        import NXOpen.UF

        uf_session = NXOpen.UF.UFSession.GetUFSession()
        image_builder = uf_session.Disp.CreateImageExportBuilder()
        image_builder.FileName = path
        image_builder.FileFormat = NXOpen.UF.ImageExportBuilder.FileFormatType.Png
        image_builder.Commit()

        return ToolResult.success(
            data={"path": path},
            message=f"Screenshot saved to {path}.",
        )

    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 5. nx_run_journal
# ---------------------------------------------------------------------------
@mcp_tool(
    name="nx_run_journal",
    description="Execute an NX journal (Python script) file.",
    params={
        "path": {
            "type": "string",
            "description": "Full file path to the journal script (.py).",
            "required": True,
        },
    },
)
async def nx_run_journal(path: str) -> ToolResult | ToolError:
    """Run an NX journal file."""
    try:
        if not os.path.isfile(path):
            return ToolError(
                error_code="NX_NOT_FOUND",
                message=f"Journal file not found: {path}",
                suggestion="Provide a valid file path to an existing .py journal script.",
            )

        ext = os.path.splitext(path)[1].lower()
        if ext != ".py":
            return ToolError(
                error_code="NX_INVALID_PARAMS",
                message=f"Journal file must be a .py file, got '{ext}'.",
                suggestion="Provide a path ending in .py.",
            )

        session = NXSession.get_instance().require()

        session.ExecuteJournal(path)

        return ToolResult.success(
            data={"path": path},
            message=f"Journal executed: {path}",
        )

    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 6. nx_record_start
# ---------------------------------------------------------------------------
@mcp_tool(
    name="nx_record_start",
    description="Start recording an NX journal.",
    params={},
)
async def nx_record_start() -> ToolResult | ToolError:
    """Begin journal recording."""
    try:
        session = NXSession.get_instance().require()

        session.BeginJournalRecording()

        return ToolResult.success(
            data={},
            message="Journal recording started.",
        )

    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 7. nx_record_stop
# ---------------------------------------------------------------------------
@mcp_tool(
    name="nx_record_stop",
    description="Stop recording an NX journal and optionally save it.",
    params={
        "save_path": {
            "type": "string",
            "description": "Optional file path to save the recorded journal.",
            "required": False,
        },
    },
)
async def nx_record_stop(save_path: str | None = None) -> ToolResult | ToolError:
    """Stop journal recording."""
    try:
        session = NXSession.get_instance().require()

        session.EndJournalRecording()

        data: dict = {}
        if save_path:
            data["save_path"] = save_path

        return ToolResult.success(
            data=data,
            message="Journal recording stopped.",
        )

    except Exception as exc:
        return ToolResult.from_exception(exc)
