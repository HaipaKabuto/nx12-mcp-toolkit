# Publication copy modified by repository maintainer on 2026-11-10.
# Original authors and licenses: docs/ATTRIBUTION.md.
"""Additive NX12 assembly/drafting tools; opt-in until real-NX acceptance.

The sidecar never imports NXOpen. File paths remain inside the configured
workspace; native work and failure rollback belong to the NX12 Loader.
"""
from __future__ import annotations

import math
from pathlib import Path
from typing import Annotated, Any, Awaitable, Callable, Literal

from mcp.server.mcpserver import MCPServer
from mcp.server.mcpserver.exceptions import ToolError as MCPToolError
from pydantic import BaseModel, ConfigDict, Field, FiniteFloat

Vector3 = Annotated[list[FiniteFloat], Field(min_length=3, max_length=3)]
Rotation9 = Annotated[list[FiniteFloat], Field(min_length=9, max_length=9)]
ToolCall = Callable[[str, dict[str, Any]], Awaitable[dict[str, Any]]]

AUTOMATION_TOOL_NAMES = {
    "nx_assembly_create", "nx_assembly_add", "nx_assembly_list",
    "nx_assembly_transform", "nx_assembly_remove", "nx_assembly_replace",
    "nx_assembly_constraint", "nx_assembly_constraints", "nx_assembly_solve",
    "nx_assembly_interference", "nx_assembly_component_geometry", "nx_assembly_save",
    "nx_assembly_delete_constraint",
    "nx_drafting_add_model_view", "nx_drafting_create_table",
    "nx_drafting_list_tables", "nx_drafting_center_mark",
    "nx_drafting_centerline", "nx_drafting_export_cad",
    "nx_drafting_update_table", "nx_drafting_detail_view",
    "nx_drafting_section_view", "nx_drafting_gdt", "nx_drafting_balloon",
}


class TableCellUpdate(BaseModel):
    model_config = ConfigDict(extra="forbid")
    row: int = Field(ge=0)
    column: int = Field(ge=0)
    text: str


def _rotation(rotation: list[float] | None) -> list[float] | None:
    """Reject scaling/reflection: component orientation must be a rigid rotation."""
    if rotation is None:
        return None
    if len(rotation) != 9 or not all(math.isfinite(v) for v in rotation):
        raise MCPToolError("Rotation must contain nine finite numbers")
    for i in range(3):
        for j in range(3):
            dot = sum(rotation[i * 3 + k] * rotation[j * 3 + k] for k in range(3))
            if abs(dot - (1.0 if i == j else 0.0)) > 1e-6:
                raise MCPToolError("Rotation must be orthonormal; scaling is not permitted")
    a, b, c, d, e, f, g, h, i = rotation
    determinant = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g)
    if abs(determinant - 1.0) > 1e-6:
        raise MCPToolError("Rotation determinant must be +1; reflection is not permitted")
    return list(rotation)


def add_automation_tools(
    mcp: MCPServer, call: ToolCall, resolve_path: Callable[[str], str],
) -> None:
    """Register explicit typed additions without changing legacy tool contracts."""
    def part_path(path: str, *, existing: bool) -> str:
        result = Path(resolve_path(path))
        if result.suffix.lower() != ".prt":
            raise MCPToolError("Assembly part paths must use the .prt extension")
        if existing and not result.is_file():
            raise MCPToolError("Component part file does not exist inside the workspace")
        if not existing and result.exists():
            raise MCPToolError("Refusing to overwrite an existing assembly part")
        return str(result)

    @mcp.tool()
    async def nx_assembly_create(path: str) -> dict[str, Any]:
        """Create a new independent NX12 assembly part inside the workspace."""
        return await call("nx_assembly_create", {"path": part_path(path, existing=False)})

    @mcp.tool()
    async def nx_assembly_add(
        path: str, name: str, parent_id: str = "/",
        position: Vector3 | None = None, rotation: Rotation9 | None = None,
    ) -> dict[str, Any]:
        """Insert an existing .prt, optionally into a subassembly.

        Position is mm. Rotation is the NX12 nine-value component orientation
        matrix in API column order; identity is the default. Returned opaque
        component IDs are used by later tools; do not substitute display names.
        """
        return await call("nx_assembly_add", {
            "path": part_path(path, existing=True), "name": name,
            "parent_id": parent_id, "position": position, "rotation": _rotation(rotation),
        })

    @mcp.tool()
    async def nx_assembly_list() -> dict[str, Any]:
        """Read the actual component hierarchy, transforms, and part references."""
        return await call("nx_assembly_list", {})

    @mcp.tool()
    async def nx_assembly_transform(
        component_id: str, position: Vector3 | None = None,
        rotation: Rotation9 | None = None, relative: bool = False,
    ) -> dict[str, Any]:
        """Set or increment component translation/orientation in owning assembly coordinates.

        Position is mm; rotation is a nine-value rigid NX orientation matrix.
        Transforms use the component definition in its direct parent prototype.
        Editing a nested definition affects every instance of that subassembly.
        List readback uses each occurrence's coordinate_part reference frame;
        do not reuse root occurrence coordinates as nested local inputs.
        """
        if position is None and rotation is None:
            raise MCPToolError("Specify position, rotation, or both")
        return await call("nx_assembly_transform", {
            "component_id": component_id, "position": position,
            "rotation": _rotation(rotation), "relative": relative,
        })

    @mcp.tool()
    async def nx_assembly_remove(component_id: str) -> dict[str, Any]:
        """Remove an assembly occurrence; its source .prt file is retained."""
        return await call("nx_assembly_remove", {"component_id": component_id})

    @mcp.tool()
    async def nx_assembly_replace(component_id: str, path: str) -> dict[str, Any]:
        """Replace an occurrence with another workspace .prt using the NX12 API."""
        return await call("nx_assembly_replace", {
            "component_id": component_id, "path": part_path(path, existing=True),
        })

    @mcp.tool()
    async def nx_assembly_component_geometry(component_id: str) -> dict[str, Any]:
        """Read occurrence face/edge selectors for assembly constraints."""
        return await call("nx_assembly_component_geometry", {"component_id": component_id})

    @mcp.tool()
    async def nx_assembly_constraint(
        kind: Literal["fix", "coaxial", "touch", "distance", "angle"],
        component_a: str, component_b: str = "", face_a: int = -1,
        face_b: int = -1, value: FiniteFloat = 0.0, name: str = "",
        alignment: Literal["infer", "same", "opposite"] = "infer",
    ) -> dict[str, Any]:
        """Create an NX12 assembly constraint and read its actual solver status.

        Face indices come from nx_assembly_component_geometry. Distance is mm;
        angle is degrees. A fix constraint requires only component_a.
        """
        if kind != "fix" and (not component_b or face_a < 0 or face_b < 0):
            raise MCPToolError("A two-component constraint needs both component IDs and face indices")
        return await call("nx_assembly_constraint", {
            "kind": kind, "component_a": component_a, "component_b": component_b,
            "face_a": face_a, "face_b": face_b, "value": value,
            "name": name, "alignment": alignment,
        })

    @mcp.tool()
    async def nx_assembly_constraints() -> dict[str, Any]:
        """Read persistent assembly constraints and their native solver states."""
        return await call("nx_assembly_constraints", {})

    @mcp.tool()
    async def nx_assembly_delete_constraint(constraint_id: str) -> dict[str, Any]:
        """Delete an explicit root-assembly constraint before repositioning its components.

        Obtain the current identifier from nx_assembly_constraints. Source
        prototype geometry and other constraints remain outside this operation.
        """
        return await call("nx_assembly_delete_constraint", {"constraint_id": constraint_id})

    @mcp.tool()
    async def nx_assembly_solve() -> dict[str, Any]:
        """Solve the active assembly constraint network and read resulting positions."""
        return await call("nx_assembly_solve", {})

    @mcp.tool()
    async def nx_assembly_save() -> dict[str, Any]:
        """Save the root and workspace-owned subassemblies with checked load/save results.

        Files outside the configured workspace must never be written. The
        Loader validates every referenced prototype before performing saves.
        """
        return await call("nx_assembly_save", {})

    @mcp.tool()
    async def nx_assembly_interference(
        component_ids: list[str] | None = None, include_touching: bool = False,
    ) -> dict[str, Any]:
        """Check actual component-pair interference; report native outcomes."""
        return await call("nx_assembly_interference", {
            "component_ids": component_ids, "include_touching": include_touching,
        })

    @mcp.tool()
    async def nx_drafting_add_model_view(
        x: FiniteFloat, y: FiniteFloat, model_view: str = "Isometric",
        scale: Annotated[FiniteFloat, Field(gt=0)] = 1.0, name: str = "",
    ) -> dict[str, Any]:
        """Place an additional named model view, including an isometric view."""
        return await call("nx_drafting_add_model_view", {
            "model_view": model_view, "x": x, "y": y, "scale": scale, "name": name,
        })

    @mcp.tool()
    async def nx_drafting_create_table(
        x: FiniteFloat, y: FiniteFloat, column_widths: list[FiniteFloat],
        row_heights: list[FiniteFloat], cells: list[list[str]], name: str = "",
        text_height: Annotated[FiniteFloat, Field(gt=0)] = 3.0,
        font: str = "chinesef",
    ) -> dict[str, Any]:
        """Create an editable NX tabular note, for title blocks or BOM tables.

        Dimensions and position are sheet mm. Text is explicit; the tool does
        not invent material, drawing numbers, or standard-compliance claims.
        """
        if not column_widths or not row_heights or any(v <= 0 for v in column_widths + row_heights):
            raise MCPToolError("Table widths and heights must be positive")
        if len(cells) != len(row_heights) or any(len(row) != len(column_widths) for row in cells):
            raise MCPToolError("Table cell shape must match its row and column dimensions")
        return await call("nx_drafting_create_table", {
            "x": x, "y": y, "column_widths": column_widths,
            "row_heights": row_heights, "cells": cells, "name": name,
            "text_height": text_height,
            "font": font,
        })

    @mcp.tool()
    async def nx_drafting_update_table(
        table_id: str, updates: Annotated[list[TableCellUpdate], Field(min_length=1)],
    ) -> dict[str, Any]:
        """Update explicit editable drawing-table cells by zero-based row/column."""
        return await call("nx_drafting_update_table", {
            "table_id": table_id, "updates": [item.model_dump() for item in updates],
        })

    @mcp.tool()
    async def nx_drafting_list_tables() -> dict[str, Any]:
        """Read actual drawing table cells, dimensions, and identifiers."""
        return await call("nx_drafting_list_tables", {})

    @mcp.tool()
    async def nx_drafting_center_mark(
        edge_index: Annotated[int, Field(ge=0)], view_id: str = "0", body_id: str = "BODY_1",
        size: Annotated[FiniteFloat, Field(gt=0)] = 5.0,
    ) -> dict[str, Any]:
        """Create a center mark on a circular model edge in the selected view."""
        return await call("nx_drafting_center_mark", {
            "view_id": view_id, "body_id": body_id, "edge_index": edge_index, "size": size,
        })

    @mcp.tool()
    async def nx_drafting_centerline(
        edge_index_1: Annotated[int, Field(ge=0)], edge_index_2: Annotated[int, Field(ge=0)],
        view_id: str = "0", body_id: str = "BODY_1",
        size: Annotated[FiniteFloat, Field(gt=0)] = 5.0,
    ) -> dict[str, Any]:
        """Create a centerline from two explicit model-edge references."""
        return await call("nx_drafting_centerline", {
            "view_id": view_id, "body_id": body_id,
            "edge_index_1": edge_index_1, "edge_index_2": edge_index_2,
            "size": size,
        })

    @mcp.tool()
    async def nx_drafting_detail_view(
        view_id: str, min_x: FiniteFloat, min_y: FiniteFloat,
        max_x: FiniteFloat, max_y: FiniteFloat, x: FiniteFloat, y: FiniteFloat,
        scale: Annotated[FiniteFloat, Field(gt=0)] = 2.0, name: str = "",
    ) -> dict[str, Any]:
        """Create a native detail view from an explicit sheet-coordinate region."""
        if min_x >= max_x or min_y >= max_y:
            raise MCPToolError("Detail region must have positive width and height")
        return await call("nx_drafting_detail_view", {
            "view_id": view_id, "min_x": min_x, "min_y": min_y,
            "max_x": max_x, "max_y": max_y, "x": x, "y": y,
            "scale": scale, "name": name,
        })

    @mcp.tool()
    async def nx_drafting_section_view(
        view_id: str, cut_x: FiniteFloat, cut_y: FiniteFloat,
        x: FiniteFloat, y: FiniteFloat,
        cut_direction: Literal["vertical", "horizontal"] = "vertical",
        scale: Annotated[FiniteFloat, Field(gt=0)] = 1.0, name: str = "",
    ) -> dict[str, Any]:
        """Create a native section view from a stated cutting-line point/direction."""
        return await call("nx_drafting_section_view", {
            "view_id": view_id, "cut_x": cut_x, "cut_y": cut_y,
            "cut_direction": cut_direction, "x": x, "y": y,
            "scale": scale, "name": name,
        })

    @mcp.tool()
    async def nx_drafting_gdt(
        x: FiniteFloat, y: FiniteFloat,
        lines: Annotated[list[str], Field(min_length=1)] | None = None,
        characteristic: Literal["flatness", "straightness", "circularity", "cylindricity"] | None = None,
        tolerance: Annotated[FiniteFloat, Field(gt=0)] | None = None,
    ) -> dict[str, Any]:
        """Create a native GD&T annotation from explicit NX-formatted tolerance text.

        Text and datum interpretation must be reviewed against the supplied
        drawing; creating a symbol does not infer a manufacturing requirement.
        """
        if characteristic is None and lines is None:
            raise MCPToolError("Specify a characteristic/tolerance pair or explicit NX-formatted lines")
        if (characteristic is None) != (tolerance is None):
            raise MCPToolError("Semantic GD&T needs both characteristic and tolerance")
        return await call("nx_drafting_gdt", {
            "lines": lines, "x": x, "y": y,
            "characteristic": characteristic, "tolerance": tolerance,
        })

    @mcp.tool()
    async def nx_drafting_balloon(
        x: FiniteFloat, y: FiniteFloat, text: str = "1",
        leader_x: FiniteFloat | None = None, leader_y: FiniteFloat | None = None,
    ) -> dict[str, Any]:
        """Create a native parts-list balloon with optional explicit leader endpoint."""
        if (leader_x is None) != (leader_y is None):
            raise MCPToolError("Balloon leader needs both leader_x and leader_y")
        return await call("nx_drafting_balloon", {
            "text": text, "x": x, "y": y, "leader_x": leader_x, "leader_y": leader_y,
        })

    @mcp.tool()
    async def nx_drafting_export_cad(
        path: str, sheet_name: str = "", format: Literal["dxf", "dwg"] = "dxf",
    ) -> dict[str, Any]:
        """Export a sheet through the NX12 translator and verify the output file."""
        resolved = Path(resolve_path(path))
        if resolved.suffix.lower() != "." + format:
            raise MCPToolError("File extension must match the requested DXF/DWG format")
        if resolved.exists():
            raise MCPToolError("Refusing to overwrite an existing drawing export")
        return await call("nx_drafting_export_cad", {
            "path": str(resolved), "sheet_name": sheet_name, "format": format,
        })
