# Publication copy modified by repository maintainer on 2026-11-10.
# Original authors and licenses: docs/ATTRIBUTION.md.
"""Assembly tools — add component, mate, list components, reposition."""

from __future__ import annotations

from typing import Any

from nx_mcp.nx_session import NXSession
from nx_mcp.response import ToolError, ToolResult
from nx_mcp.tools.registry import mcp_tool

# ---------------------------------------------------------------------------
# Mate type mapping
# ---------------------------------------------------------------------------
_MATE_TYPES = {
    "touch": "Touch",
    "align": "Align",
    "orient": "Orient",
    "center": "Center",
    "align_angle": "AlignAngle",
}


# ---------------------------------------------------------------------------
# 1. nx_add_component
# ---------------------------------------------------------------------------
@mcp_tool(
    name="nx_add_component",
    description="Add a component from a .prt file to the current assembly.",
    params={
        "part_path": {
            "type": "string",
            "description": "Full file path of the .prt part file to add as a component.",
            "required": True,
        },
        "name": {
            "type": "string",
            "description": "Optional display name for the new component.",
            "required": False,
        },
    },
)
async def nx_add_component(
    part_path: str,
    name: str | None = None,
) -> ToolResult | ToolError:
    """Add a component from a .prt file into the current assembly."""
    try:
        work_part = NXSession.get_instance().require_work_part()

        comp_assembly = work_part.ComponentAssembly
        component = comp_assembly.AddComponent(part_path, name or "")

        comp_name = (
            component.Name()
            if callable(getattr(component, "Name", None))
            else getattr(component, "Name", name or part_path)
        )

        return ToolResult.success(
            data={"component": comp_name, "part_path": part_path},
            message=f"Added component '{comp_name}' from {part_path}.",
        )

    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 2. nx_mate_component
# ---------------------------------------------------------------------------
@mcp_tool(
    name="nx_mate_component",
    description="Apply a mating constraint between assembly components.",
    params={
        "component": {
            "type": "string",
            "description": "Name of the component to apply the mate to.",
            "required": True,
        },
        "mate_type": {
            "type": "string",
            "description": "Type of mate: touch, align, orient, center, align_angle.",
            "required": True,
        },
        "references": {
            "type": "array",
            "description": "List of reference names (edges, faces, datums) for the mate.",
            "required": False,
        },
        "offset": {
            "type": "number",
            "description": "Offset distance for the mate constraint. Default is 0.",
            "required": False,
        },
    },
)
async def nx_mate_component(
    component: str,
    mate_type: str,
    references: list[str] | None = None,
    offset: float = 0.0,
) -> ToolResult | ToolError:
    """Apply a mating constraint between assembly components."""
    try:
        import NXOpen

        work_part = NXSession.get_instance().require_work_part()

        mate_key = mate_type.strip().lower()
        if mate_key not in _MATE_TYPES:
            valid = ", ".join(_MATE_TYPES.keys())
            return ToolError(
                error_code="NX_INVALID_PARAMS",
                message=f"Unsupported mate type: '{mate_type}'.",
                suggestion=f"Use one of: {valid}.",
            )

        comp_assembly = work_part.ComponentAssembly
        constraints_builder = comp_assembly.CreateConstraintsBuilder()

        type_map = {
            "touch": NXOpen.Assemblies.Constraint.Type.Touch,
            "align": NXOpen.Assemblies.Constraint.Type.Align,
            "orient": NXOpen.Assemblies.Constraint.Type.Orient,
            "center": NXOpen.Assemblies.Constraint.Type.Center,
            "align_angle": NXOpen.Assemblies.Constraint.Type.Angular,
        }
        constraint_type = type_map[mate_key]

        constraint = constraints_builder.CreateConstraint(constraint_type)
        constraint.Component = component

        if offset != 0.0:
            constraint.Offset = offset

        if references:
            for i, ref in enumerate(references):
                if i == 0:
                    constraint.FirstConstraintReference = ref
                elif i == 1:
                    constraint.SecondConstraintReference = ref

        constraints_builder.Commit()
        constraints_builder.Destroy()

        result_data: dict[str, Any] = {
            "component": component,
            "mate_type": mate_type,
            "offset": offset,
        }
        if references:
            result_data["references"] = references

        return ToolResult.success(
            data=result_data,
            message=f"Applied '{mate_type}' mate to component '{component}'.",
        )

    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 3. nx_list_components
# ---------------------------------------------------------------------------
@mcp_tool(
    name="nx_list_components",
    description="List all components in the current assembly.",
    params={},
)
async def nx_list_components() -> ToolResult | ToolError:
    """List all components in the current work part's assembly."""
    try:
        work_part = NXSession.get_instance().require_work_part()

        comp_assembly = work_part.ComponentAssembly
        root_component = comp_assembly.RootComponent

        components_list: list[dict[str, Any]] = []

        if root_component is not None:
            children = root_component.GetChildren()
            for child in children:
                child_name = (
                    child.Name()
                    if callable(getattr(child, "Name", None))
                    else getattr(child, "Name", "unknown")
                )
                child_data: dict[str, Any] = {"name": str(child_name)}
                components_list.append(child_data)

        return ToolResult.success(
            data={"components": components_list, "count": len(components_list)},
            message=f"Assembly contains {len(components_list)} component(s).",
        )

    except Exception as exc:
        return ToolResult.from_exception(exc)


# ---------------------------------------------------------------------------
# 4. nx_reposition_component
# ---------------------------------------------------------------------------
@mcp_tool(
    name="nx_reposition_component",
    description="Move a component by translation and rotation offsets.",
    params={
        "component": {
            "type": "string",
            "description": "Name of the component to reposition.",
            "required": True,
        },
        "dx": {
            "type": "number",
            "description": "Translation offset along X axis. Default is 0.",
            "required": False,
        },
        "dy": {
            "type": "number",
            "description": "Translation offset along Y axis. Default is 0.",
            "required": False,
        },
        "dz": {
            "type": "number",
            "description": "Translation offset along Z axis. Default is 0.",
            "required": False,
        },
        "rx": {
            "type": "number",
            "description": "Rotation offset around X axis in degrees. Default is 0.",
            "required": False,
        },
        "ry": {
            "type": "number",
            "description": "Rotation offset around Y axis in degrees. Default is 0.",
            "required": False,
        },
        "rz": {
            "type": "number",
            "description": "Rotation offset around Z axis in degrees. Default is 0.",
            "required": False,
        },
    },
)
async def nx_reposition_component(
    component: str,
    dx: float = 0.0,
    dy: float = 0.0,
    dz: float = 0.0,
    rx: float = 0.0,
    ry: float = 0.0,
    rz: float = 0.0,
) -> ToolResult | ToolError:
    """Reposition a component by translation and rotation offsets."""
    try:
        import math

        import NXOpen

        work_part = NXSession.get_instance().require_work_part()

        comp_assembly = work_part.ComponentAssembly
        root_component = comp_assembly.RootComponent
        target = None
        if root_component is not None:
            for child in root_component.GetChildren():
                child_name = (
                    child.Name()
                    if callable(getattr(child, "Name", None))
                    else getattr(child, "Name", "")
                )
                if child_name == component:
                    target = child
                    break

        if target is None:
            return ToolError(
                error_code="NX_NOT_FOUND",
                message=f"Component '{component}' not found in assembly.",
                suggestion="Use nx_list_components to see available components.",
            )

        transform = NXOpen.Transform()
        transform.Translation = NXOpen.Vector3d(dx, dy, dz)

        cx, sx = math.cos(math.radians(rx)), math.sin(math.radians(rx))
        cy, sy = math.cos(math.radians(ry)), math.sin(math.radians(ry))
        cz, sz = math.cos(math.radians(rz)), math.sin(math.radians(rz))

        rot = NXOpen.Matrix3x3()
        rot.Xx = cy * cz
        rot.Xy = sx * sy * cz - cx * sz
        rot.Xz = cx * sy * cz + sx * sz
        rot.Yx = cy * sz
        rot.Yy = sx * sy * sz + cx * cz
        rot.Yz = cx * sy * sz - sx * cz
        rot.Zx = -sy
        rot.Zy = sx * cy
        rot.Zz = cx * cy
        transform.Rotation = rot

        comp_assembly.MoveComponent(target, transform)

        return ToolResult.success(
            data={
                "component": component,
                "translation": [dx, dy, dz],
                "rotation": [rx, ry, rz],
            },
            message=f"Repositioned component '{component}' "
            f"(dx={dx}, dy={dy}, dz={dz}, rx={rx}, ry={ry}, rz={rz}).",
        )

    except Exception as exc:
        return ToolResult.from_exception(exc)
