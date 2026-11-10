# Publication copy modified by repository maintainer on 2026-11-10.
# Original authors and licenses: docs/ATTRIBUTION.md.
"""Sketch tools -- create sketch, draw lines/arcs/rectangles, add constraints,
finish sketch."""

from __future__ import annotations

import logging
from typing import Any

from nx_mcp.nx_session import NXSession
from nx_mcp.response import ToolError, ToolResult
from nx_mcp.tools.registry import mcp_tool

logger = logging.getLogger("nx_mcp")

# Valid plane names and their normal direction vectors
_PLANE_NORMALS: dict[str, tuple[float, float, float]] = {
    "XY": (0.0, 0.0, 1.0),
    "XZ": (0.0, 1.0, 0.0),
    "YZ": (1.0, 0.0, 0.0),
}

# ---------------------------------------------------------------------------
# 1. nx_create_sketch
# ---------------------------------------------------------------------------


@mcp_tool(
    name="nx_create_sketch",
    description="Create a new sketch on the specified reference plane (XY, XZ, or YZ).",
    params={
        "plane": {
            "type": "string",
            "description": "Reference plane for the sketch: XY, XZ, or YZ. Default is XY.",
            "required": False,
        },
        "name": {
            "type": "string",
            "description": "Optional name for the sketch.",
            "required": False,
        },
    },
)
async def nx_create_sketch(
    plane: str = "XY",
    name: str | None = None,
) -> ToolResult | ToolError:
    try:
        import NXOpen

        work_part = NXSession.get_instance().require_work_part()

        key = plane.strip().upper()
        if key not in _PLANE_NORMALS:
            valid = ", ".join(_PLANE_NORMALS.keys())
            return ToolError(
                error_code="NX_INVALID_PARAMS",
                message=f"Invalid plane '{plane}'. Use one of: {valid}.",
            )

        nx_dir, ny_dir, nz_dir = _PLANE_NORMALS[key]
        normal = NXOpen.Vector3d(nx_dir, ny_dir, nz_dir)

        builder = work_part.Sketches.CreateSketchBuilder()
        builder.SetPlaneNormal(normal)

        if name:
            builder.SetName(name)

        sketch = builder.Commit()
        sketch_name = sketch.Name if sketch else name or "Sketch"
        builder.Destroy()

        return ToolResult.success(
            data={"sketch": sketch_name, "plane": key},
            message=f"Created sketch '{sketch_name}' on {key} plane.",
        )
    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 2. nx_sketch_line
# ---------------------------------------------------------------------------


@mcp_tool(
    name="nx_sketch_line",
    description="Create a line in the active sketch from (x1, y1) to (x2, y2).",
    params={
        "x1": {"type": "number", "description": "Start point X coordinate.", "required": True},
        "y1": {"type": "number", "description": "Start point Y coordinate.", "required": True},
        "x2": {"type": "number", "description": "End point X coordinate.", "required": True},
        "y2": {"type": "number", "description": "End point Y coordinate.", "required": True},
    },
)
async def nx_sketch_line(
    x1: float,
    y1: float,
    x2: float,
    y2: float,
) -> ToolResult | ToolError:
    try:
        import NXOpen

        work_part = NXSession.get_instance().require_work_part()

        start = NXOpen.Point3d(x1, y1, 0.0)
        end = NXOpen.Point3d(x2, y2, 0.0)

        builder = work_part.Curves.CreateLineBuilder()
        builder.SetStartPoint(start)
        builder.SetEndPoint(end)

        curve = builder.Commit()
        curve_name = curve.Name if curve else "Line"
        builder.Destroy()

        return ToolResult.success(
            data={"curve": curve_name, "start": [x1, y1], "end": [x2, y2]},
            message=f"Created line from ({x1}, {y1}) to ({x2}, {y2}).",
        )
    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 3. nx_sketch_arc
# ---------------------------------------------------------------------------


@mcp_tool(
    name="nx_sketch_arc",
    description="Create an arc in the active sketch given center, radius, start and end angles.",
    params={
        "cx": {"type": "number", "description": "Center X coordinate.", "required": True},
        "cy": {"type": "number", "description": "Center Y coordinate.", "required": True},
        "radius": {"type": "number", "description": "Arc radius.", "required": True},
        "start_angle": {
            "type": "number",
            "description": "Start angle in degrees.",
            "required": True,
        },
        "end_angle": {"type": "number", "description": "End angle in degrees.", "required": True},
    },
)
async def nx_sketch_arc(
    cx: float,
    cy: float,
    radius: float,
    start_angle: float,
    end_angle: float,
) -> ToolResult | ToolError:
    try:
        import NXOpen

        work_part = NXSession.get_instance().require_work_part()

        center = NXOpen.Point3d(cx, cy, 0.0)

        builder = work_part.Curves.CreateArcBuilder()
        builder.SetCenter(center)
        builder.SetRadius(radius)
        builder.SetStartAngle(start_angle)
        builder.SetEndAngle(end_angle)

        curve = builder.Commit()
        curve_name = curve.Name if curve else "Arc"
        builder.Destroy()

        return ToolResult.success(
            data={
                "curve": curve_name,
                "center": [cx, cy],
                "radius": radius,
                "start_angle": start_angle,
                "end_angle": end_angle,
            },
            message=f"Created arc at ({cx}, {cy}) with radius {radius}, "
            f"from {start_angle} deg to {end_angle} deg.",
        )
    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 4. nx_sketch_rectangle
# ---------------------------------------------------------------------------


@mcp_tool(
    name="nx_sketch_rectangle",
    description="Create a rectangle in the active sketch from corner (x1, y1) to (x2, y2).",
    params={
        "x1": {"type": "number", "description": "First corner X.", "required": True},
        "y1": {"type": "number", "description": "First corner Y.", "required": True},
        "x2": {"type": "number", "description": "Opposite corner X.", "required": True},
        "y2": {"type": "number", "description": "Opposite corner Y.", "required": True},
    },
)
async def nx_sketch_rectangle(
    x1: float,
    y1: float,
    x2: float,
    y2: float,
) -> ToolResult | ToolError:
    try:
        import NXOpen

        work_part = NXSession.get_instance().require_work_part()

        # Four edges of the rectangle
        corners = [
            (x1, y1),
            (x2, y1),
            (x2, y2),
            (x1, y2),
        ]
        created_lines: list[str] = []

        for i in range(4):
            sx, sy = corners[i]
            ex, ey = corners[(i + 1) % 4]

            start_pt = NXOpen.Point3d(sx, sy, 0.0)
            end_pt = NXOpen.Point3d(ex, ey, 0.0)

            builder = work_part.Curves.CreateLineBuilder()
            builder.SetStartPoint(start_pt)
            builder.SetEndPoint(end_pt)

            curve = builder.Commit()
            created_lines.append(curve.Name if curve else f"Line_{i}")
            builder.Destroy()

        return ToolResult.success(
            data={
                "curves": created_lines,
                "corner1": [x1, y1],
                "corner2": [x2, y2],
                "width": abs(x2 - x1),
                "height": abs(y2 - y1),
            },
            message=f"Created rectangle from ({x1}, {y1}) to ({x2}, {y2}).",
        )
    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 5. nx_sketch_constraint
# ---------------------------------------------------------------------------

_GEOMETRIC_CONSTRAINT_TYPES = {
    "horizontal",
    "vertical",
    "parallel",
    "perpendicular",
    "tangent",
    "equal_length",
    "fix",
    "coincident",
    "midpoint",
    "concentric",
}

_DIMENSIONAL_CONSTRAINT_TYPES = {
    "dimension",
}


@mcp_tool(
    name="nx_sketch_constraint",
    description="Apply a constraint to sketch geometry (horizontal, vertical, parallel, etc.).",
    params={
        "constraint_type": {
            "type": "string",
            "description": "Type of constraint to apply.",
            "required": True,
        },
        "targets": {
            "type": "array",
            "description": "List of target geometry names to constrain.",
            "required": True,
        },
        "value": {
            "type": "number",
            "description": "Optional numeric value for dimensional constraints.",
            "required": False,
        },
    },
)
async def nx_sketch_constraint(
    constraint_type: str,
    targets: list[str],
    value: float | None = None,
) -> ToolResult | ToolError:
    try:
        import NXOpen

        work_part = NXSession.get_instance().require_work_part()

        key = constraint_type.strip().lower()
        all_valid = _GEOMETRIC_CONSTRAINT_TYPES | _DIMENSIONAL_CONSTRAINT_TYPES
        if key not in all_valid:
            valid = ", ".join(sorted(all_valid))
            return ToolError(
                error_code="NX_INVALID_PARAMS",
                message=f"Invalid constraint type '{constraint_type}'.",
                suggestion=f"Use one of: {valid}.",
            )

        if key in _GEOMETRIC_CONSTRAINT_TYPES:
            # Map constraint type names to NXOpen.Sketch.GeometricConstraintType enums
            geo_map = {
                "horizontal": NXOpen.Sketch.GeometricConstraintType.Horizontal,
                "vertical": NXOpen.Sketch.GeometricConstraintType.Vertical,
                "parallel": NXOpen.Sketch.GeometricConstraintType.Parallel,
                "perpendicular": NXOpen.Sketch.GeometricConstraintType.Perpendicular,
                "tangent": NXOpen.Sketch.GeometricConstraintType.Tangent,
                "equal_length": NXOpen.Sketch.GeometricConstraintType.EqualLength,
                "fix": NXOpen.Sketch.GeometricConstraintType.Fixed,
                "coincident": NXOpen.Sketch.GeometricConstraintType.Coincident,
                "midpoint": NXOpen.Sketch.GeometricConstraintType.Midpoint,
                "concentric": NXOpen.Sketch.GeometricConstraintType.Concentric,
            }
            constraint_enum = geo_map[key]
            sketch = work_part.Sketches.ActiveSketch
            if sketch is None:
                return ToolError(
                    error_code="NX_NO_ACTIVE_SKETCH",
                    message="No active sketch. Use nx_create_sketch first.",
                )
            sketch.CreateGeometricConstraint(constraint_enum, targets)
        else:
            # Dimensional constraint
            if value is None:
                return ToolError(
                    error_code="NX_INVALID_PARAMS",
                    message="Dimensional constraints require a 'value' parameter.",
                )
            sketch = work_part.Sketches.ActiveSketch
            if sketch is None:
                return ToolError(
                    error_code="NX_NO_ACTIVE_SKETCH",
                    message="No active sketch. Use nx_create_sketch first.",
                )
            sketch.CreateDimension(
                NXOpen.Sketch.ConstraintType.HorizontalDimension,
                targets,
                value,
            )

        data: dict[str, Any] = {
            "constraint_type": key,
            "targets": targets,
        }
        if value is not None:
            data["value"] = value

        return ToolResult.success(
            data=data,
            message=f"Applied '{key}' constraint to {len(targets)} target(s).",
        )
    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 6. nx_finish_sketch
# ---------------------------------------------------------------------------


@mcp_tool(
    name="nx_finish_sketch",
    description="Finish (exit) the currently active sketch.",
    params={},
)
async def nx_finish_sketch() -> ToolResult | ToolError:
    try:
        import NXOpen

        work_part = NXSession.get_instance().require_work_part()

        active_sketch = work_part.Sketches.ActiveSketch
        if active_sketch is not None:
            active_sketch.Deactivate(
                NXOpen.Sketch.ViewOrientation.TrueValue,
                NXOpen.Sketch.CloseLevel.FalseValue,
            )

        return ToolResult.success(
            data={},
            message="Sketch finished successfully.",
        )
    except Exception as exc:
        return ToolResult.from_exception(exc)
