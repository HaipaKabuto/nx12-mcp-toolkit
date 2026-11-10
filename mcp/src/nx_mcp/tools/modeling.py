# Publication copy modified by repository maintainer on 2026-11-10.
# Original authors and licenses: docs/ATTRIBUTION.md.
"""Modeling tools — extrude, revolve, sweep, blend, chamfer, hole, pattern,
boolean, delete/edit feature, mirror body."""

from __future__ import annotations

from typing import Any

from nx_mcp.nx_session import NXSession
from nx_mcp.response import ToolError, ToolResult
from nx_mcp.tools.registry import mcp_tool

# ---------------------------------------------------------------------------
# Helper
# ---------------------------------------------------------------------------


def _direction_vector(direction: str):
    """Return an NXOpen Vector3 for a cardinal direction string."""
    import NXOpen

    table = {
        "X": NXOpen.Vector3d(1.0, 0.0, 0.0),
        "Y": NXOpen.Vector3d(0.0, 1.0, 0.0),
        "Z": NXOpen.Vector3d(0.0, 0.0, 1.0),
        "-X": NXOpen.Vector3d(-1.0, 0.0, 0.0),
        "-Y": NXOpen.Vector3d(0.0, -1.0, 0.0),
        "-Z": NXOpen.Vector3d(0.0, 0.0, -1.0),
    }
    key = direction.strip().upper()
    if key not in table:
        raise ValueError(f"Invalid direction '{direction}'. Use one of: {', '.join(table)}")
    return table[key]


def _boolean_option(boolean: str):
    """Map a boolean string to NXOpen.Feature.BooleanType or None."""
    import NXOpen

    mapping = {
        "none": None,
        "unite": NXOpen.Feature.BooleanType.Unite,
        "subtract": NXOpen.Feature.BooleanType.Subtract,
        "intersect": NXOpen.Feature.BooleanType.Intersect,
    }
    key = boolean.strip().lower()
    if key not in mapping:
        raise ValueError(f"Invalid boolean type '{boolean}'. Use: none, unite, subtract, intersect")
    return mapping[key]


# ---------------------------------------------------------------------------
# 1. nx_extrude
# ---------------------------------------------------------------------------


@mcp_tool(
    name="nx_extrude",
    description="Extrude a section or sketch by a given distance.",
    params={
        "distance": {"type": "number", "description": "Extrusion distance", "required": True},
        "direction": {
            "type": "string",
            "description": "Extrusion direction (X, Y, Z, -X, -Y, -Z)",
            "required": False,
        },
        "boolean": {
            "type": "string",
            "description": "Boolean operation: none, unite, subtract, intersect",
            "required": False,
        },
        "sketch_name": {
            "type": "string",
            "description": "Name of the sketch to extrude (optional)",
            "required": False,
        },
    },
)
async def nx_extrude(
    distance: float,
    direction: str = "Z",
    boolean: str = "none",
    sketch_name: str | None = None,
) -> ToolResult | ToolError:
    try:
        import NXOpen

        work_part = NXSession.get_instance().require_work_part()

        # Resolve sketch if name provided
        sketch = None
        if sketch_name:
            for sk in work_part.Sketches.ToArray():
                if sk.Name == sketch_name or sk.Name.endswith(sketch_name):
                    sketch = sk
                    break
            if sketch is None:
                return ToolError(
                    error_code="NX_NOT_FOUND",
                    message=f"Sketch '{sketch_name}' not found.",
                    suggestion="Use nx_list_sketches to see available sketches.",
                )

        dir_vec = _direction_vector(direction)
        bool_type = _boolean_option(boolean)

        builder = work_part.Features.CreateExtrudeBuilder(None)
        builder.SetDistance(
            NXOpen.Expression.ValueType.Double, distance, NXOpen.Unit.CollectionType.Millimeter
        )
        builder.SetDirection(dir_vec)
        if bool_type is not None:
            builder.SetBooleanOperation(bool_type)
        if sketch is not None:
            builder.SetSketch(sketch)

        feature = builder.Commit()
        feature_name = feature.Name if feature else "Extrude"
        builder.Destroy()

        return ToolResult.success(
            data={"feature": feature_name, "distance": distance, "direction": direction},
            message=f"Extruded '{feature_name}' by {distance} mm along {direction}.",
        )
    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 2. nx_revolve
# ---------------------------------------------------------------------------


@mcp_tool(
    name="nx_revolve",
    description="Revolve a section or sketch around an axis.",
    params={
        "angle": {
            "type": "number",
            "description": "Revolution angle in degrees (default 360)",
            "required": False,
        },
        "axis": {
            "type": "string",
            "description": "Axis of revolution (X, Y, Z)",
            "required": False,
        },
        "sketch_name": {
            "type": "string",
            "description": "Name of the sketch to revolve",
            "required": False,
        },
        "boolean": {
            "type": "string",
            "description": "Boolean operation: none, unite, subtract, intersect",
            "required": False,
        },
    },
)
async def nx_revolve(
    angle: float = 360.0,
    axis: str = "Z",
    sketch_name: str | None = None,
    boolean: str = "none",
) -> ToolResult | ToolError:
    try:
        import NXOpen

        work_part = NXSession.get_instance().require_work_part()

        # Resolve sketch if name provided
        sketch = None
        if sketch_name:
            for sk in work_part.Sketches.ToArray():
                if sk.Name == sketch_name or sk.Name.endswith(sketch_name):
                    sketch = sk
                    break
            if sketch is None:
                return ToolError(
                    error_code="NX_NOT_FOUND",
                    message=f"Sketch '{sketch_name}' not found.",
                    suggestion="Use nx_list_sketches to see available sketches.",
                )

        axis_vec = _direction_vector(axis)
        bool_type = _boolean_option(boolean)

        builder = work_part.Features.CreateRevolveBuilder(None)
        builder.SetAngle(
            NXOpen.Expression.ValueType.Double, angle, NXOpen.Unit.CollectionType.Degree
        )
        builder.SetAxis(axis_vec)
        if bool_type is not None:
            builder.SetBooleanOperation(bool_type)
        if sketch is not None:
            builder.SetSketch(sketch)

        feature = builder.Commit()
        feature_name = feature.Name if feature else "Revolve"
        builder.Destroy()

        return ToolResult.success(
            data={"feature": feature_name, "angle": angle, "axis": axis},
            message=f"Revolved '{feature_name}' by {angle} degrees around {axis}.",
        )
    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 3. nx_sweep
# ---------------------------------------------------------------------------


@mcp_tool(
    name="nx_sweep",
    description="Sweep a section along a guide curve.",
    params={
        "section": {"type": "string", "description": "Name of the section curve", "required": True},
        "guide": {"type": "string", "description": "Name of the guide curve", "required": True},
        "boolean": {
            "type": "string",
            "description": "Boolean operation: none, unite, subtract, intersect",
            "required": False,
        },
    },
)
async def nx_sweep(
    section: str,
    guide: str,
    boolean: str = "none",
) -> ToolResult | ToolError:
    try:
        from nx_mcp.utils.geometry import resolve_object_by_name

        work_part = NXSession.get_instance().require_work_part()

        section_obj = resolve_object_by_name(
            work_part, section, work_part.Sketches, work_part.Curves
        )
        if section_obj is None:
            return ToolError(
                error_code="NX_NOT_FOUND",
                message=f"Section '{section}' not found.",
                suggestion="Use nx_list_sketches to see available sketches.",
            )

        guide_obj = resolve_object_by_name(work_part, guide, work_part.Sketches, work_part.Curves)
        if guide_obj is None:
            return ToolError(
                error_code="NX_NOT_FOUND",
                message=f"Guide '{guide}' not found.",
                suggestion="Use nx_list_sketches to see available sketches.",
            )

        bool_type = _boolean_option(boolean)

        builder = work_part.Features.CreateSweepBuilder(None)
        builder.SetSection(section_obj)
        builder.SetGuide(guide_obj)
        if bool_type is not None:
            builder.SetBooleanOperation(bool_type)

        feature = builder.Commit()
        feature_name = feature.Name if feature else "Sweep"
        builder.Destroy()

        return ToolResult.success(
            data={"feature": feature_name, "section": section, "guide": guide},
            message=f"Swept '{feature_name}' along guide '{guide}'.",
        )
    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 4. nx_blend
# ---------------------------------------------------------------------------


@mcp_tool(
    name="nx_blend",
    description="Create an edge blend (fillet) on specified edges.",
    params={
        "edges": {
            "type": "array",
            "description": "List of edge names to blend",
            "required": True,
        },
        "radius": {"type": "number", "description": "Blend radius", "required": True},
    },
)
async def nx_blend(
    edges: list[str],
    radius: float,
) -> ToolResult | ToolError:
    try:
        import NXOpen

        work_part = NXSession.get_instance().require_work_part()

        builder = work_part.Features.CreateEdgeBlendBuilder(None)
        builder.SetRadius(
            NXOpen.Expression.ValueType.Double, radius, NXOpen.Unit.CollectionType.Millimeter
        )
        for edge_name in edges:
            builder.AddEdge(edge_name)

        feature = builder.Commit()
        feature_name = feature.Name if feature else "Blend"
        builder.Destroy()

        return ToolResult.success(
            data={"feature": feature_name, "edges": edges, "radius": radius},
            message=f"Blended {len(edges)} edge(s) with radius {radius} mm.",
        )
    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 5. nx_chamfer
# ---------------------------------------------------------------------------


@mcp_tool(
    name="nx_chamfer",
    description="Create a chamfer on specified edges.",
    params={
        "edges": {
            "type": "array",
            "description": "List of edge names to chamfer",
            "required": True,
        },
        "offset": {"type": "number", "description": "Chamfer offset distance", "required": True},
    },
)
async def nx_chamfer(
    edges: list[str],
    offset: float,
) -> ToolResult | ToolError:
    try:
        import NXOpen

        work_part = NXSession.get_instance().require_work_part()

        builder = work_part.Features.CreateChamferBuilder(None)
        builder.SetOffset(
            NXOpen.Expression.ValueType.Double, offset, NXOpen.Unit.CollectionType.Millimeter
        )
        for edge_name in edges:
            builder.AddEdge(edge_name)

        feature = builder.Commit()
        feature_name = feature.Name if feature else "Chamfer"
        builder.Destroy()

        return ToolResult.success(
            data={"feature": feature_name, "edges": edges, "offset": offset},
            message=f"Chamfered {len(edges)} edge(s) with offset {offset} mm.",
        )
    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 6. nx_hole
# ---------------------------------------------------------------------------


@mcp_tool(
    name="nx_hole",
    description="Create a hole feature at the specified location.",
    params={
        "diameter": {"type": "number", "description": "Hole diameter", "required": True},
        "depth": {"type": "number", "description": "Hole depth", "required": True},
        "x": {"type": "number", "description": "X coordinate of hole center", "required": True},
        "y": {"type": "number", "description": "Y coordinate of hole center", "required": True},
        "z": {"type": "number", "description": "Z coordinate of hole center", "required": True},
    },
)
async def nx_hole(
    diameter: float,
    depth: float,
    x: float,
    y: float,
    z: float,
) -> ToolResult | ToolError:
    try:
        import NXOpen

        work_part = NXSession.get_instance().require_work_part()

        builder = work_part.Features.CreateHoleBuilder(None)
        builder.SetDiameter(
            NXOpen.Expression.ValueType.Double, diameter, NXOpen.Unit.CollectionType.Millimeter
        )
        builder.SetDepth(
            NXOpen.Expression.ValueType.Double, depth, NXOpen.Unit.CollectionType.Millimeter
        )
        builder.SetLocation(x, y, z)

        feature = builder.Commit()
        feature_name = feature.Name if feature else "Hole"
        builder.Destroy()

        return ToolResult.success(
            data={
                "feature": feature_name,
                "diameter": diameter,
                "depth": depth,
                "location": [x, y, z],
            },
            message=f"Created hole '{feature_name}' (dia={diameter}, depth={depth}) at ({x}, {y}, {z}).",
        )
    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 7. nx_pattern
# ---------------------------------------------------------------------------


@mcp_tool(
    name="nx_pattern",
    description="Create a linear pattern of features.",
    params={
        "features": {
            "type": "array",
            "description": "List of feature names to pattern",
            "required": True,
        },
        "pattern_type": {
            "type": "string",
            "description": "Pattern type (e.g. linear, circular)",
            "required": True,
        },
        "count": {"type": "integer", "description": "Number of instances", "required": True},
        "spacing": {"type": "number", "description": "Spacing between instances", "required": True},
        "direction": {
            "type": "string",
            "description": "Pattern direction (X, Y, Z)",
            "required": True,
        },
    },
)
async def nx_pattern(
    features: list[str],
    pattern_type: str,
    count: int,
    spacing: float,
    direction: str,
) -> ToolResult | ToolError:
    try:
        import NXOpen

        work_part = NXSession.get_instance().require_work_part()
        dir_vec = _direction_vector(direction)

        builder = work_part.Features.CreatePatternBuilder(None)
        builder.SetPatternType(pattern_type)
        builder.SetCount(count)
        builder.SetSpacing(
            NXOpen.Expression.ValueType.Double, spacing, NXOpen.Unit.CollectionType.Millimeter
        )
        builder.SetDirection(dir_vec)
        for feat_name in features:
            builder.AddFeature(feat_name)

        feature = builder.Commit()
        feature_name = feature.Name if feature else "Pattern"
        builder.Destroy()

        return ToolResult.success(
            data={
                "feature": feature_name,
                "features": features,
                "pattern_type": pattern_type,
                "count": count,
                "spacing": spacing,
            },
            message=f"Patterned {len(features)} feature(s) x{count} with spacing {spacing} mm.",
        )
    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 8. nx_boolean
# ---------------------------------------------------------------------------


@mcp_tool(
    name="nx_boolean",
    description="Perform a boolean operation (unite, subtract, intersect).",
    params={
        "boolean_type": {
            "type": "string",
            "description": "Boolean operation: unite, subtract, intersect",
            "required": True,
        },
        "targets": {
            "type": "array",
            "description": "List of target body names",
            "required": True,
        },
    },
)
async def nx_boolean(
    boolean_type: str,
    targets: list[str],
) -> ToolResult | ToolError:
    try:
        import NXOpen

        work_part = NXSession.get_instance().require_work_part()

        type_map = {
            "unite": NXOpen.Feature.BooleanType.Unite,
            "subtract": NXOpen.Feature.BooleanType.Subtract,
            "intersect": NXOpen.Feature.BooleanType.Intersect,
        }
        key = boolean_type.strip().lower()
        if key not in type_map:
            return ToolError(
                error_code="NX_INVALID_PARAMS",
                message=f"Invalid boolean_type '{boolean_type}'. Use: unite, subtract, intersect.",
            )

        builder = work_part.Features.CreateBooleanBuilder(None)
        builder.SetBooleanOperation(type_map[key])
        for target_name in targets:
            builder.AddTarget(target_name)

        feature = builder.Commit()
        feature_name = feature.Name if feature else "Boolean"
        builder.Destroy()

        return ToolResult.success(
            data={"feature": feature_name, "boolean_type": boolean_type, "targets": targets},
            message=f"Boolean {boolean_type} applied to {len(targets)} target(s).",
        )
    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 9. nx_delete_feature
# ---------------------------------------------------------------------------


@mcp_tool(
    name="nx_delete_feature",
    description="Delete a feature by name. Returns available features if not found.",
    params={
        "name": {
            "type": "string",
            "description": "Name of the feature to delete",
            "required": True,
        },
    },
)
async def nx_delete_feature(name: str) -> ToolResult | ToolError:
    try:
        work_part = NXSession.get_instance().require_work_part()

        found = None
        available: list[str] = []
        for feat in work_part.Features.ToArray():
            feat_name = feat.Name
            available.append(feat_name)
            if feat_name == name:
                found = feat

        if found is None:
            return ToolError(
                error_code="NX_NOT_FOUND",
                message=f"Feature '{name}' not found.",
                suggestion=f"Available features: {', '.join(available[:30])}",
            )

        work_part.Features.Delete(found)
        return ToolResult.success(
            data={"deleted": name},
            message=f"Feature '{name}' deleted.",
        )
    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 10. nx_edit_feature
# ---------------------------------------------------------------------------


@mcp_tool(
    name="nx_edit_feature",
    description="Edit a feature's parameters by updating its expressions.",
    params={
        "name": {"type": "string", "description": "Feature name to edit", "required": True},
        "params": {
            "type": "object",
            "description": "Key-value pairs of parameter names to new values",
            "required": True,
        },
    },
)
async def nx_edit_feature(
    name: str,
    params: dict[str, Any],
) -> ToolResult | ToolError:
    try:
        work_part = NXSession.get_instance().require_work_part()

        found = None
        for feat in work_part.Features.ToArray():
            if feat.Name == name:
                found = feat
                break

        if found is None:
            return ToolError(
                error_code="NX_NOT_FOUND",
                message=f"Feature '{name}' not found.",
            )

        updated: dict[str, Any] = {}
        for param_name, new_value in params.items():
            expr = found.GetExpression(param_name)
            expr.SetFormula(str(new_value))
            updated[param_name] = new_value

        return ToolResult.success(
            data={"feature": name, "updated": updated},
            message=f"Feature '{name}' updated: {updated}.",
        )
    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 11. nx_mirror_body
# ---------------------------------------------------------------------------


@mcp_tool(
    name="nx_mirror_body",
    description="Mirror a body across a datum plane.",
    params={
        "body": {"type": "string", "description": "Name of the body to mirror", "required": True},
        "plane": {
            "type": "string",
            "description": "Name of the datum plane for mirroring",
            "required": True,
        },
    },
)
async def nx_mirror_body(
    body: str,
    plane: str,
) -> ToolResult | ToolError:
    try:
        work_part = NXSession.get_instance().require_work_part()

        builder = work_part.Features.CreateMirrorBodyBuilder(None)
        builder.SetBody(body)
        builder.SetMirrorPlane(plane)

        feature = builder.Commit()
        feature_name = feature.Name if feature else "Mirror"
        builder.Destroy()

        return ToolResult.success(
            data={"feature": feature_name, "body": body, "plane": plane},
            message=f"Mirrored body '{body}' across plane '{plane}'.",
        )
    except Exception as exc:
        return ToolResult.from_exception(exc)
