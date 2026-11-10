# Publication copy modified by repository maintainer on 2026-11-10.
# Original authors and licenses: docs/ATTRIBUTION.md.
"""Drawing tools — create drawings, add views, dimensions, and export to PDF."""

from __future__ import annotations

from nx_mcp.nx_session import NXSession
from nx_mcp.response import ToolError, ToolResult
from nx_mcp.tools.registry import mcp_tool

# ---------------------------------------------------------------------------
# Valid lookup tables
# ---------------------------------------------------------------------------
_SHEET_SIZES = {"A0", "A1", "A2", "A3", "A4", "A", "B", "C", "D", "E"}

_VIEW_TYPES = {
    "front": "Front",
    "top": "Top",
    "right": "Right",
    "left": "Left",
    "bottom": "Bottom",
    "back": "Back",
    "isometric": "Isometric",
    "trimetric": "Trimetric",
}

_PROJECTION_DIRECTIONS = {"right", "left", "top", "bottom"}

_DIM_TYPES = {"horizontal", "vertical", "aligned", "diameter", "radius", "angle"}


# ---------------------------------------------------------------------------
# 1. nx_create_drawing
# ---------------------------------------------------------------------------
@mcp_tool(
    name="nx_create_drawing",
    description="Create a new drawing sheet in the current work part.",
    params={
        "name": {
            "type": "string",
            "description": "Name for the new drawing sheet. Default is 'Sheet1'.",
            "required": False,
        },
        "size": {
            "type": "string",
            "description": "Sheet size: A0-A4 or A-E. Default is 'A3'.",
            "required": False,
        },
        "scale": {
            "type": "number",
            "description": "Drawing scale numerator (e.g. 1.0 for 1:1, 2.0 for 2:1). Default is 1.0.",
            "required": False,
        },
    },
)
async def nx_create_drawing(
    name: str = "Sheet1",
    size: str = "A3",
    scale: float = 1.0,
) -> ToolResult | ToolError:
    """Create a new drawing sheet in the current work part."""
    try:
        size_upper = size.upper().strip()
        if size_upper not in _SHEET_SIZES:
            valid = ", ".join(sorted(_SHEET_SIZES))
            return ToolError(
                error_code="NX_INVALID_PARAMS",
                message=f"Unsupported sheet size: '{size}'.",
                suggestion=f"Use one of: {valid}.",
            )

        work_part = NXSession.get_instance().require_work_part()

        builder = work_part.DrawingSheets.CreateDrawingSheetBuilder()
        builder.Name = name
        builder.ScaleNumerator = scale
        builder.ScaleDenominator = 1.0
        builder.Size = size_upper

        sheet = builder.Commit()
        builder.Destroy()

        return ToolResult.success(
            data={
                "sheet_name": sheet.Name,
                "size": size_upper,
                "scale": f"{scale}:1",
            },
            message=f"Created drawing sheet '{sheet.Name}' (size={size_upper}, scale={scale}:1).",
        )

    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 2. nx_add_base_view
# ---------------------------------------------------------------------------
@mcp_tool(
    name="nx_add_base_view",
    description="Add a base view (model view) to a drawing sheet.",
    params={
        "drawing": {
            "type": "string",
            "description": "Name of the target drawing sheet.",
            "required": True,
        },
        "body": {
            "type": "string",
            "description": "Name or handle of the solid body to create the view from.",
            "required": True,
        },
        "view": {
            "type": "string",
            "description": "View orientation: front, top, right, left, bottom, back, isometric, trimetric.",
            "required": True,
        },
    },
)
async def nx_add_base_view(drawing: str, body: str, view: str) -> ToolResult | ToolError:
    """Add a base view to a drawing sheet."""
    try:
        view_lower = view.lower().strip()
        if view_lower not in _VIEW_TYPES:
            valid = ", ".join(sorted(_VIEW_TYPES.keys()))
            return ToolError(
                error_code="NX_INVALID_PARAMS",
                message=f"Unsupported view orientation: '{view}'.",
                suggestion=f"Use one of: {valid}.",
            )

        work_part = NXSession.get_instance().require_work_part()

        sheets = work_part.DrawingSheets.ToArray()
        target_sheet = None
        for s in sheets:
            if s.Name == drawing:
                target_sheet = s
                break

        if target_sheet is None:
            return ToolError(
                error_code="NX_NOT_FOUND",
                message=f"Drawing sheet '{drawing}' not found.",
                suggestion="Use nx_create_drawing to create a sheet first.",
            )

        view_builder = work_part.DrawingViews.CreateBaseViewBuilder()
        view_builder.Style.Orientation = _VIEW_TYPES[view_lower]
        view_builder.Style.ScaleNumerator = 1.0
        view_builder.Style.ScaleDenominator = 1.0
        view_builder.Sheet = target_sheet

        created_view = view_builder.Commit()
        view_builder.Destroy()

        return ToolResult.success(
            data={
                "view_name": created_view.Name,
                "drawing": drawing,
                "orientation": view_lower,
                "body": body,
            },
            message=f"Added base view '{created_view.Name}' ({view_lower}) to sheet '{drawing}'.",
        )

    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 3. nx_add_projection_view
# ---------------------------------------------------------------------------
@mcp_tool(
    name="nx_add_projection_view",
    description="Add a projected view derived from an existing base view.",
    params={
        "base_view": {
            "type": "string",
            "description": "Name of the parent base view to project from.",
            "required": True,
        },
        "direction": {
            "type": "string",
            "description": "Projection direction: right, left, top, bottom.",
            "required": True,
        },
    },
)
async def nx_add_projection_view(base_view: str, direction: str) -> ToolResult | ToolError:
    """Add a projected view from an existing base view."""
    try:
        dir_lower = direction.lower().strip()
        if dir_lower not in _PROJECTION_DIRECTIONS:
            valid = ", ".join(sorted(_PROJECTION_DIRECTIONS))
            return ToolError(
                error_code="NX_INVALID_PARAMS",
                message=f"Unsupported projection direction: '{direction}'.",
                suggestion=f"Use one of: {valid}.",
            )

        work_part = NXSession.get_instance().require_work_part()

        views = work_part.DrawingViews.ToArray()
        parent_view = None
        for v in views:
            if v.Name == base_view:
                parent_view = v
                break

        if parent_view is None:
            return ToolError(
                error_code="NX_NOT_FOUND",
                message=f"Base view '{base_view}' not found.",
                suggestion="Ensure the base view name is correct.",
            )

        proj_builder = work_part.DrawingViews.CreateProjectedViewBuilder()
        proj_builder.ParentView = parent_view
        proj_builder.Style.Orientation = dir_lower.capitalize()
        proj_builder.Style.ScaleNumerator = 1.0
        proj_builder.Style.ScaleDenominator = 1.0

        created_view = proj_builder.Commit()
        proj_builder.Destroy()

        return ToolResult.success(
            data={
                "view_name": created_view.Name,
                "base_view": base_view,
                "direction": dir_lower,
            },
            message=f"Added projection view '{created_view.Name}' ({dir_lower}) from '{base_view}'.",
        )

    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 4. nx_add_dimension
# ---------------------------------------------------------------------------
@mcp_tool(
    name="nx_add_dimension",
    description="Add a dimension annotation to a drawing view.",
    params={
        "view": {
            "type": "string",
            "description": "Name of the drawing view to add the dimension to.",
            "required": True,
        },
        "object1": {
            "type": "string",
            "description": "First geometry object (edge/face) reference for the dimension.",
            "required": True,
        },
        "object2": {
            "type": "string",
            "description": "Second geometry object reference (optional for diameter/radius).",
            "required": False,
        },
        "dim_type": {
            "type": "string",
            "description": "Dimension type: horizontal, vertical, aligned, diameter, radius, angle.",
            "required": True,
        },
    },
)
async def nx_add_dimension(
    view: str,
    object1: str,
    object2: str | None = None,
    dim_type: str = "aligned",
) -> ToolResult | ToolError:
    """Add a dimension annotation to a drawing view."""
    try:
        dim_lower = dim_type.lower().strip()
        if dim_lower not in _DIM_TYPES:
            valid = ", ".join(sorted(_DIM_TYPES))
            return ToolError(
                error_code="NX_INVALID_PARAMS",
                message=f"Unsupported dimension type: '{dim_type}'.",
                suggestion=f"Use one of: {valid}.",
            )

        if dim_lower in ("diameter", "radius") and object2 is not None:
            return ToolError(
                error_code="NX_INVALID_PARAMS",
                message=f"Dimension type '{dim_lower}' uses a single object; object2 should be omitted.",
                suggestion="Set object2 to null or omit it for diameter/radius dimensions.",
            )

        work_part = NXSession.get_instance().require_work_part()

        views = work_part.DrawingViews.ToArray()
        target_view = None
        for v in views:
            if v.Name == view:
                target_view = v
                break

        if target_view is None:
            return ToolError(
                error_code="NX_NOT_FOUND",
                message=f"Drawing view '{view}' not found.",
                suggestion="Check view name with nx_list_open_parts or create it first.",
            )

        dim_builder = work_part.Annotations.CreateDimensionBuilder()
        dim_builder.DimensionType = dim_lower.capitalize()
        dim_builder.Object1 = object1
        if object2 is not None:
            dim_builder.Object2 = object2
        dim_builder.View = target_view

        created_dim = dim_builder.Commit()
        dim_builder.Destroy()

        return ToolResult.success(
            data={
                "dimension_name": created_dim.Name,
                "view": view,
                "dim_type": dim_lower,
                "object1": object1,
                "object2": object2,
            },
            message=f"Added {dim_lower} dimension '{created_dim.Name}' to view '{view}'.",
        )

    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 5. nx_export_drawing_pdf
# ---------------------------------------------------------------------------
@mcp_tool(
    name="nx_export_drawing_pdf",
    description="Export the current drawing sheet to a PDF file.",
    params={
        "path": {
            "type": "string",
            "description": "Full file path for the exported PDF file.",
            "required": True,
        },
    },
)
async def nx_export_drawing_pdf(path: str) -> ToolResult | ToolError:
    """Export the current drawing to PDF."""
    try:
        work_part = NXSession.get_instance().require_work_part()

        exporter = work_part.ExportManager.CreatePdfExporter()
        exporter.OutputFile = path
        exporter.Apply()

        return ToolResult.success(
            data={"path": path},
            message=f"Exported drawing to PDF: {path}",
        )

    except Exception as exc:
        return ToolResult.from_exception(exc)
