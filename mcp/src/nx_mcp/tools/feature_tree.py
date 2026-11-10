# Publication copy modified by repository maintainer on 2026-11-10.
# Original authors and licenses: docs/ATTRIBUTION.md.
"""Feature tree tools for querying NX part features and bounding boxes."""

from __future__ import annotations

from nx_mcp.nx_session import NXSession
from nx_mcp.response import ToolError, ToolResult
from nx_mcp.tools.registry import mcp_tool


@mcp_tool(
    name="nx_list_features",
    description="List all features in the current work part.",
    params={},
)
async def nx_list_features() -> ToolResult | ToolError:
    """Return a list of features with name, type, and timestamp."""
    try:
        session = NXSession.get_instance()
        work_part = session.require_work_part()

        features = work_part.Features.ToArray()
        feature_list = []
        for f in features:
            feature_list.append(
                {
                    "name": f.Name,
                    "type": f.FeatureType,
                    "timestamp": f.Timestamp,
                }
            )

        return ToolResult.success(
            data={"features": feature_list, "count": len(feature_list)},
            message=f"Found {len(feature_list)} feature(s).",
        )
    except Exception as exc:
        return ToolResult.from_exception(exc)


@mcp_tool(
    name="nx_get_feature_info",
    description="Get detailed information about a specific feature by name.",
    params={
        "name": {
            "type": "string",
            "description": "Name of the feature to look up (case-insensitive).",
            "required": True,
        },
    },
)
async def nx_get_feature_info(name: str) -> ToolResult | ToolError:
    """Return name, type, timestamp, and expressions for a feature."""
    try:
        session = NXSession.get_instance()
        work_part = session.require_work_part()

        features = work_part.Features.ToArray()
        target = None
        for f in features:
            if f.Name.lower() == name.lower():
                target = f
                break

        if target is None:
            available = [f.Name for f in features]
            return ToolError(
                error_code="NX_NOT_FOUND",
                message=f"Feature '{name}' not found.",
                suggestion=f"Available features: {available}",
            )

        expressions = []
        for expr in target.GetExpressions():
            expressions.append(
                {
                    "name": expr.Name,
                    "value": expr.Value,
                }
            )

        return ToolResult.success(
            data={
                "name": target.Name,
                "type": target.FeatureType,
                "timestamp": target.Timestamp,
                "expressions": expressions,
            },
            message=f"Feature '{target.Name}' details.",
        )
    except Exception as exc:
        return ToolResult.from_exception(exc)


@mcp_tool(
    name="nx_get_bounding_box",
    description="Get the bounding box of a body in the current work part.",
    params={
        "body": {
            "type": "string",
            "description": "Name of the body (optional; uses first body if not specified).",
            "required": False,
        },
    },
)
async def nx_get_bounding_box(body: str | None = None) -> ToolResult | ToolError:
    """Return min/max points and dimensions for a body's bounding box."""
    try:
        session = NXSession.get_instance()
        work_part = session.require_work_part()

        bodies = work_part.Bodies.ToArray()
        if not bodies:
            return ToolError(
                error_code="NX_NOT_FOUND",
                message="No bodies found in the current work part.",
            )

        target = None
        if body is not None:
            for b in bodies:
                if b.Name.lower() == body.lower():
                    target = b
                    break
            if target is None:
                available = [b.Name for b in bodies]
                return ToolError(
                    error_code="NX_NOT_FOUND",
                    message=f"Body '{body}' not found.",
                    suggestion=f"Available bodies: {available}",
                )
        else:
            target = bodies[0]

        bbox = target.GetBoundingBox()

        min_point = {"x": bbox[0], "y": bbox[1], "z": bbox[2]}
        max_point = {"x": bbox[3], "y": bbox[4], "z": bbox[5]}

        dimensions = {
            "length_x": round(max_point["x"] - min_point["x"], 6),
            "length_y": round(max_point["y"] - min_point["y"], 6),
            "length_z": round(max_point["z"] - min_point["z"], 6),
        }

        return ToolResult.success(
            data={
                "body": target.Name,
                "min": min_point,
                "max": max_point,
                "dimensions": dimensions,
            },
            message=f"Bounding box for body '{target.Name}'.",
        )
    except Exception as exc:
        return ToolResult.from_exception(exc)
