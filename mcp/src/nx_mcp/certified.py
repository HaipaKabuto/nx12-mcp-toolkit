# Publication copy modified by repository maintainer on 2026-11-10.
# Original authors and licenses: docs/ATTRIBUTION.md.
"""The explicitly certified v0.2 MCP tool surface."""

from __future__ import annotations

import json
from typing import Annotated, Any, Literal, Protocol

from mcp.server.mcpserver import MCPServer
from mcp.server.mcpserver.exceptions import ToolError as MCPToolError
from pydantic import BaseModel, Field

from nx_mcp import __version__
from nx_mcp.contracts import (
    ExportResult,
    ExtrudeResult,
    NXToolError,
    ObjectListResult,
    ObjectResult,
    OperationResult,
    PartResult,
    Point2D,
    StatusResult,
)
from nx_mcp.workspace import Workspace, WorkspaceViolation


class Point3D(BaseModel):
    """A 3D point in model space (mm)."""

    x: float = Field(allow_inf_nan=False)
    y: float = Field(allow_inf_nan=False)
    z: float = Field(allow_inf_nan=False)


class BridgeCaller(Protocol):
    async def call(self, method: str, params: dict[str, Any]) -> dict[str, Any]: ...


CERTIFIED_TOOL_NAMES = {
    "nx_status",
    "nx_create_part",
    "nx_open_part",
    "nx_save_part",
    "nx_close_part",
    "nx_export_step",
    "nx_list_sketches",
    "nx_list_bodies",
    "nx_list_features",
    "nx_list_edges",
    "nx_list_faces",
    "nx_create_sketch",
    "nx_sketch_line",
    "nx_sketch_rectangle",
    "nx_sketch_circle",
    "nx_sketch_arc",
    "nx_finish_sketch",
    "nx_extrude",
    "nx_hole",
    "nx_counterbore_hole",
    "nx_countersink_hole",
    "nx_shell",
    "nx_edge_blend",
    "nx_chamfer",
    "nx_unite",
    "nx_revolve",
    "nx_mirror",
    "nx_linear_pattern",
    "nx_circular_pattern",
    "nx_undo",
    "nx_fit_view",
    "nx_release",
    # drafting (2D drawing) - backed by the C# Loader on NX 12
    "nx_create_drawing_sheet",
    "nx_create_projected_view",
    "nx_list_drawing_sheets",
    "nx_list_drafting_views",
    "nx_update_views",
    "nx_create_dimension",
    "nx_list_dimensions",
    "nx_list_annotations",
    "nx_create_note",
    "nx_create_diameter_dimension",
    "nx_create_radius_dimension",
    "nx_delete_annotation",
    "nx_export_pdf",
    "nx_measure_distance",
    "nx_create_roughness",
    "nx_create_title_block",
    "nx_set_attribute",
    "nx_list_attributes",
    # NOTE: nx_create_sheet_border is implemented but NOT exposed. Commit() always
    # fails with "宽度必须大于零或小于水平或竖直区域大小..." (invalid zone width).
    # Likely cause: NXOpen.Diagramming is NX's *diagramming* (schematic) app, not
    # the drafting app, so it does not apply to drafting sheets. Zone sizes were
    # set (no warnings in log) and it still failed, so the route is wrong.
    # NOTE: nx_create_detail_view is implemented and compiles, but Commit() always
    # fails with "用于定义边界的点不能相同" (boundary points must differ) no matter how
    # the two points are supplied. Hidden until the real boundary API is found.
    # NOTE: nx_export_dwg is implemented in the Loader but NOT exposed: Commit()
    # succeeds yet no file is written. See memory for everything already tried.
    # Exposing a tool that silently does nothing is worse than not having it.
    # NOTE: nx_create_centerline is implemented in the Loader but NOT exposed yet.
    # AutomaticCenterlineBuilder.Commit() fails with NX status 1175325 even after
    # adding the drafting view to its Views list. Exposing a broken tool is worse
    # than not having it, so it stays hidden until the cause is found.
}


def create_certified_server(
    bridge: BridgeCaller,
    workspace: Workspace | None = None,
    *,
    enable_experimental: bool = False,
    enable_journal: bool = False,
) -> MCPServer:
    mcp = MCPServer(
        "nx-mcp",
        instructions="Certified Siemens NX tools for a local NX session.",
        version=__version__,
    )

    async def call(method: str, params: dict[str, Any]) -> dict[str, Any]:
        try:
            return await bridge.call(method, params)
        except NXToolError as error:
            raise MCPToolError(json.dumps(error.as_dict(), ensure_ascii=False)) from error

    def resolve_path(path: str) -> str:
        if workspace is None:
            raise MCPToolError(
                json.dumps(
                    NXToolError(
                        "NX_WORKSPACE_NOT_CONFIGURED",
                        "NX_MCP_WORKSPACE must be configured before file operations are used.",
                    ).as_dict(),
                    ensure_ascii=False,
                )
            )
        try:
            return str(workspace.resolve(path))
        except WorkspaceViolation as error:
            tool_error = NXToolError("NX_PATH_OUTSIDE_WORKSPACE", str(error))
            raise MCPToolError(json.dumps(tool_error.as_dict(), ensure_ascii=False)) from error

    @mcp.tool()
    async def nx_status() -> StatusResult:
        """Report bridge, NX version, and active-part status."""
        return StatusResult(**await call("nx_status", {}))

    @mcp.tool()
    async def nx_create_part(path: str, units: Literal["mm", "inch"] = "mm") -> PartResult:
        """Create a part inside the configured workspace."""
        return PartResult(
            **await call("nx_create_part", {"path": resolve_path(path), "units": units})
        )

    @mcp.tool()
    async def nx_open_part(path: str) -> PartResult:
        """Open a part from the configured workspace."""
        return PartResult(**await call("nx_open_part", {"path": resolve_path(path)}))

    @mcp.tool()
    async def nx_save_part() -> OperationResult:
        """Save the active work part."""
        return OperationResult(**await call("nx_save_part", {}))

    @mcp.tool()
    async def nx_close_part(save: bool = True) -> OperationResult:
        """Close the active work part, optionally saving it first."""
        return OperationResult(**await call("nx_close_part", {"save": save}))

    @mcp.tool()
    async def nx_export_step(path: str) -> ExportResult:
        """Export the active work part as STEP inside the configured workspace."""
        return ExportResult(**await call("nx_export_step", {"path": resolve_path(path)}))

    @mcp.tool()
    async def nx_list_sketches() -> ObjectListResult:
        """List sketches in the active work part."""
        return ObjectListResult(**await call("nx_list_sketches", {}))

    @mcp.tool()
    async def nx_list_bodies() -> ObjectListResult:
        """List bodies in the active work part."""
        return ObjectListResult(**await call("nx_list_bodies", {}))

    @mcp.tool()
    async def nx_list_features() -> ObjectListResult:
        """List features in the active work part."""
        return ObjectListResult(**await call("nx_list_features", {}))

    @mcp.tool()
    async def nx_list_edges(body_id: str) -> dict[str, Any]:
        """Inspect every edge of a body: index, curve type, endpoints, midpoint,
        length, bounding box, direction and adjacent face count. Read-only."""
        return await call("nx_list_edges", {"body_id": body_id})

    @mcp.tool()
    async def nx_list_faces(body_id: str) -> dict[str, Any]:
        """Inspect every face of a body: index, tag, face type, centroid, area,
        normal (planar only) and adjacent edge count. Read-only."""
        return await call("nx_list_faces", {"body_id": body_id})

    @mcp.tool()
    async def nx_create_sketch(
        plane: Literal["XY", "XZ", "YZ"] = "XY", name: str | None = None
    ) -> ObjectResult:
        """Create and activate a sketch on a principal datum plane."""
        return ObjectResult(**await call("nx_create_sketch", {"plane": plane, "name": name}))

    @mcp.tool()
    async def nx_sketch_line(sketch_id: str, start: Point2D, end: Point2D) -> ObjectResult:
        """Add a line to an explicit sketch reference."""
        return ObjectResult(
            **await call(
                "nx_sketch_line",
                {"sketch_id": sketch_id, "start": start.model_dump(), "end": end.model_dump()},
            )
        )

    @mcp.tool()
    async def nx_sketch_rectangle(
        sketch_id: str, corner1: Point2D, corner2: Point2D
    ) -> ObjectListResult:
        """Add a rectangle to an explicit sketch reference."""
        return ObjectListResult(
            **await call(
                "nx_sketch_rectangle",
                {
                    "sketch_id": sketch_id,
                    "corner1": corner1.model_dump(),
                    "corner2": corner2.model_dump(),
                },
            )
        )

    @mcp.tool()
    async def nx_sketch_circle(
        sketch_id: str,
        center: Point2D,
        diameter: Annotated[float, Field(gt=0, allow_inf_nan=False)],
    ) -> ObjectResult:
        """Add a circle (center + diameter) to an explicit sketch reference."""
        return ObjectResult(
            **await call(
                "nx_sketch_circle",
                {"sketch_id": sketch_id, "center": center.model_dump(), "diameter": diameter},
            )
        )

    @mcp.tool()
    async def nx_sketch_arc(
        sketch_id: str,
        center: Point2D,
        radius: Annotated[float, Field(gt=0, allow_inf_nan=False)],
        start_angle: Annotated[float, Field(ge=0, allow_inf_nan=False)] = 0.0,
        end_angle: Annotated[float, Field(ge=0, allow_inf_nan=False)] = 90.0,
    ) -> ObjectResult:
        """Add an arc (center, radius, start/end degrees) to an explicit sketch reference."""
        return ObjectResult(
            **await call(
                "nx_sketch_arc",
                {
                    "sketch_id": sketch_id,
                    "center": center.model_dump(),
                    "radius": radius,
                    "start_angle": start_angle,
                    "end_angle": end_angle,
                },
            )
        )

    @mcp.tool()
    async def nx_finish_sketch(sketch_id: str) -> ObjectResult:
        """Deactivate and finish an explicit sketch reference."""
        return ObjectResult(**await call("nx_finish_sketch", {"sketch_id": sketch_id}))

    @mcp.tool()
    async def nx_extrude(
        sketch_id: str,
        distance: Annotated[float, Field(gt=0, allow_inf_nan=False)],
        reverse: bool = False,
        start_offset: Annotated[float, Field(ge=0, allow_inf_nan=False)] = 0.0,
        operation: Literal["create", "subtract"] = "create",
        target_body_id: str | None = None,
    ) -> ExtrudeResult:
        """Extrude a sketch into a new body, or subtract it from an existing target body."""
        return ExtrudeResult(
            **await call(
                "nx_extrude",
                {
                    "sketch_id": sketch_id,
                    "distance": distance,
                    "reverse": reverse,
                    "start_offset": start_offset,
                    "operation": operation,
                    "target_body_id": target_body_id,
                },
            )
        )

    @mcp.tool()
    async def nx_hole(
        body_id: str,
        center: Point2D,
        diameter: Annotated[float, Field(gt=0, allow_inf_nan=False)],
        depth: Annotated[float, Field(gt=0, allow_inf_nan=False)],
        start_offset: Annotated[float, Field(ge=0, allow_inf_nan=False)] = 0.0,
    ) -> ObjectResult:
        """Create a hole on a target body (circle sketch + boolean subtract)."""
        return ObjectResult(
            **await call(
                "nx_hole",
                {
                    "body_id": body_id,
                    "center": center.model_dump(),
                    "diameter": diameter,
                    "depth": depth,
                    "start_offset": start_offset,
                },
            )
        )

    @mcp.tool()
    async def nx_counterbore_hole(
        body_id: str,
        center: Point2D,
        hole_diameter: Annotated[float, Field(gt=0, allow_inf_nan=False)],
        hole_depth: Annotated[float, Field(gt=0, allow_inf_nan=False)],
        counterbore_diameter: Annotated[float, Field(gt=0, allow_inf_nan=False)],
        counterbore_depth: Annotated[float, Field(gt=0, allow_inf_nan=False)],
        start_offset: Annotated[float, Field(ge=0, allow_inf_nan=False)] = 0.0,
    ) -> ObjectResult:
        """Create a coaxial counterbore hole: a smaller deep through hole plus a
        larger shallow counterbore from the top face. Independent from nx_hole."""
        if counterbore_diameter <= hole_diameter:
            raise ValueError("counterbore_diameter must be > hole_diameter")
        if counterbore_depth >= hole_depth:
            raise ValueError("counterbore_depth must be < hole_depth")
        return ObjectResult(
            **await call(
                "nx_counterbore_hole",
                {
                    "body_id": body_id,
                    "center": center.model_dump(),
                    "hole_diameter": hole_diameter,
                    "hole_depth": hole_depth,
                    "counterbore_diameter": counterbore_diameter,
                    "counterbore_depth": counterbore_depth,
                    "start_offset": start_offset,
                },
            )
        )

    @mcp.tool()
    async def nx_countersink_hole(
        body_id: str,
        center: Point2D,
        hole_diameter: Annotated[float, Field(gt=0, allow_inf_nan=False)],
        hole_depth: Annotated[float, Field(gt=0, allow_inf_nan=False)],
        countersink_diameter: Annotated[float, Field(gt=0, allow_inf_nan=False)],
        countersink_angle: Annotated[float, Field(gt=0, lt=180, allow_inf_nan=False)] = 90.0,
        start_offset: Annotated[float, Field(ge=0, allow_inf_nan=False)] = 0.0,
    ) -> ObjectResult:
        """Create a coaxial countersink (conical) hole: a smaller deep through
        cylinder plus a conical countersink from the top face. Independent of
        nx_hole / nx_counterbore_hole."""
        if countersink_diameter <= hole_diameter:
            raise ValueError("countersink_diameter must be > hole_diameter")
        return ObjectResult(
            **await call(
                "nx_countersink_hole",
                {
                    "body_id": body_id,
                    "center": center.model_dump(),
                    "hole_diameter": hole_diameter,
                    "hole_depth": hole_depth,
                    "countersink_diameter": countersink_diameter,
                    "countersink_angle": countersink_angle,
                    "start_offset": start_offset,
                },
            )
        )

    @mcp.tool()
    async def nx_shell(
        body_id: str,
        thickness: Annotated[float, Field(gt=0, allow_inf_nan=False)],
        remove_face_index: int,
        inward: bool = True,
    ) -> ObjectResult:
        """Shell (hollow) a body: remove one face as an opening and keep a
        constant wall thickness. First version removes a single face; inward=True
        keeps the outer dimensions."""
        return ObjectResult(
            **await call(
                "nx_shell",
                {
                    "body_id": body_id,
                    "thickness": thickness,
                    "remove_face_index": remove_face_index,
                    "inward": inward,
                },
            )
        )

    @mcp.tool()
    async def nx_edge_blend(
        body_id: str,
        radius: Annotated[float, Field(gt=0, allow_inf_nan=False)],
        edge_indices: list[int] | None = None,
    ) -> ObjectResult:
        """Create an edge blend (fillet) on edges of a body; default all edges."""
        return ObjectResult(
            **await call(
                "nx_edge_blend",
                {"body_id": body_id, "radius": radius, "edge_indices": edge_indices},
            )
        )

    @mcp.tool()
    async def nx_chamfer(
        body_id: str,
        offset: Annotated[float, Field(gt=0, allow_inf_nan=False)],
        edge_indices: list[int] | None = None,
    ) -> ObjectResult:
        """Create a symmetric chamfer on edges of a body; default all edges."""
        return ObjectResult(
            **await call(
                "nx_chamfer",
                {"body_id": body_id, "offset": offset, "edge_indices": edge_indices},
            )
        )

    @mcp.tool()
    async def nx_unite(
        target_body_id: str,
        tool_body_ids: list[str],
    ) -> ObjectResult:
        """Boolean-unite one or more tool bodies into the target body.

        The target body keeps its id; the tool bodies are consumed and
        disappear from ``nx_list_bodies`` after the boolean.
        """
        return ObjectResult(
            **await call(
                "nx_unite",
                {"target_body_id": target_body_id, "tool_body_ids": tool_body_ids},
            )
        )

    @mcp.tool()
    async def nx_revolve(
        sketch_id: str,
        axis_start: Point2D,
        axis_end: Point2D,
        angle: Annotated[float, Field(gt=0, le=360, allow_inf_nan=False)] = 360.0,
        reverse: bool = False,
    ) -> ObjectResult:
        """Revolve a closed sketch section around an in-sketch 2D axis.

        Creates a new solid only (no unite/subtract). ``axis_start`` and
        ``axis_end`` are sketch XY coordinates defining the revolution axis;
        ``angle`` is the sweep angle in degrees (0 < angle <= 360).
        """
        return ObjectResult(
            **await call(
                "nx_revolve",
                {
                    "sketch_id": sketch_id,
                    "axis_start": axis_start.model_dump(),
                    "axis_end": axis_end.model_dump(),
                    "angle": angle,
                    "reverse": reverse,
                },
            )
        )

    @mcp.tool()
    async def nx_mirror(
        body_id: str,
        plane: Literal["XY", "XZ", "YZ"],
        offset: Annotated[float, Field(allow_inf_nan=False)] = 0.0,
    ) -> ObjectResult:
        """Mirror a body about a datum plane.

        ``plane`` selects the mirror datum plane (XY/XZ/YZ); ``offset`` shifts
        that plane along its normal by the given mm value (default 0). The
        mirrored body is created as an independent body and is NOT united with
        the source; use ``nx_unite`` if merging is required.
        """
        return ObjectResult(
            **await call(
                "nx_mirror",
                {"body_id": body_id, "plane": plane, "offset": offset},
            )
        )

    @mcp.tool()
    async def nx_linear_pattern(
        body_id: str,
        direction: Literal["X", "Y", "Z"],
        count: Annotated[int, Field(ge=2, le=1000)],
        spacing: Annotated[float, Field(gt=0, allow_inf_nan=False)],
        reverse: bool = False,
    ) -> ObjectListResult:
        """Pattern a body linearly along X/Y/Z.

        ``count`` is the total number of instances including the original
        (count=4 yields 4 bodies); ``spacing`` is the per-instance delta in mm.
        The original body is kept and new instances are independent bodies
        (NOT united); use ``nx_unite`` if merging is required.
        """
        return ObjectListResult(
            **await call(
                "nx_linear_pattern",
                {
                    "body_id": body_id,
                    "direction": direction,
                    "count": count,
                    "spacing": spacing,
                    "reverse": reverse,
                },
            )
        )

    @mcp.tool()
    async def nx_circular_pattern(
        body_id: str,
        axis: Literal["X", "Y", "Z"],
        center: Point3D,
        count: Annotated[int, Field(ge=2, le=1000)],
        angle: Annotated[float, Field(gt=0, le=360, allow_inf_nan=False)],
        reverse: bool = False,
    ) -> ObjectListResult:
        """Pattern a body circularly about an axis through ``center``.

        ``count`` is the total number of instances including the original
        (count=4 yields 4 bodies); ``angle`` is the total sweep in degrees
        (360.0 distributes evenly around a full circle). The original body is
        kept and new instances are independent bodies (NOT united); use
        ``nx_unite`` if merging is required.
        """
        return ObjectListResult(
            **await call(
                "nx_circular_pattern",
                {
                    "body_id": body_id,
                    "axis": axis,
                    "center": {"x": center.x, "y": center.y, "z": center.z},
                    "count": count,
                    "angle": angle,
                    "reverse": reverse,
                },
            )
        )

    @mcp.tool()
    async def nx_undo() -> OperationResult:
        """Undo the last visible NX MCP operation."""
        return OperationResult(**await call("nx_undo", {}))

    @mcp.tool()
    async def nx_fit_view() -> OperationResult:
        """Fit the active modeling view."""
        return OperationResult(**await call("nx_fit_view", {}))

    @mcp.tool()
    async def nx_release() -> OperationResult:
        """Clear the current task state and return NX to normal interaction.

        With the resident C# Loader backend, the Loader and named pipe remain
        alive for the entire NX session; nx_release does not unload the plugin
        or require Alt+F8 to start it again.
        """
        return OperationResult(**await call("nx_release", {}))

    # ---- drafting (2D drawing) -------------------------------------------
    # Executed by the resident C# Loader. Sheet and view names are tokenised in
    # the pipe protocol, so they must not contain spaces.

    @mcp.tool()
    async def nx_create_drawing_sheet(
        name: str = "MCP_SHEET_1",
        size: Literal["A0", "A1", "A2", "A3", "A4"] = "A4",
        projection: Literal["first", "third"] = "first",
        scale: float = 1.0,
        model_view: str = "Top",
    ) -> OperationResult:
        """Create a drawing sheet and place a base view of the given model view.

        The part must already contain geometry for the base view to be meaningful.
        """
        return OperationResult(
            **await call(
                "nx_create_drawing_sheet",
                {
                    "name": name,
                    "size": size,
                    "projection": projection,
                    "scale": scale,
                    "model_view": model_view,
                },
            )
        )

    @mcp.tool()
    async def nx_create_projected_view(
        x: float,
        y: float,
        parent_view_id: str = "",
        name: str = "",
    ) -> OperationResult:
        """Project a view from an existing drafting view at sheet position (x, y).

        Leave parent_view_id empty to use the first drafting view on the sheet.
        """
        return OperationResult(
            **await call(
                "nx_create_projected_view",
                {"x": x, "y": y, "parent_view_id": parent_view_id, "name": name},
            )
        )

    @mcp.tool()
    async def nx_list_drawing_sheets() -> OperationResult:
        """List the drawing sheets in the active part."""
        return OperationResult(**await call("nx_list_drawing_sheets", {}))

    @mcp.tool()
    async def nx_list_drafting_views() -> OperationResult:
        """List the drafting views on the current drawing sheet."""
        return OperationResult(**await call("nx_list_drafting_views", {}))

    @mcp.tool()
    async def nx_list_dimensions() -> OperationResult:
        """Read back every dimension in the part: measured size and tolerance.

        Returns one entry per dimension, formatted as
        "<journal_id>|<name>|size=<value>|tol=<upper>/<lower>|<tolerance_type>".
        Use this to verify dimensions you just created, or to READ an existing
        NX 2D drawing: sizes and tolerances are recovered from the model, so an
        agent can understand what a drawing specifies without opening the UI.
        """
        return OperationResult(**await call("nx_list_dimensions", {}))

    @mcp.tool()
    async def nx_list_annotations() -> OperationResult:
        """Read the non-dimension annotations of a drawing: notes, GD&T, roughness.

        Each entry is prefixed by kind:
          "NOTE|<journal_id>|<text>"            drafting / technical-requirement text
          "GDT|<journal_id>|<characteristic>|tol=<value>|datum=<d1>,<d2>,<d3>"
          "ROUGH|<journal_id>"                  surface finish symbol
        Use this together with nx_list_dimensions to fully understand an existing
        NX 2D process drawing.
        """
        return OperationResult(**await call("nx_list_annotations", {}))

    @mcp.tool()
    async def nx_create_radius_dimension(
        view_id: str = "",
        body_id: str = "",
        edge_index: int = 0,
        pos_x: float = 0.0,
        pos_y: float = 0.0,
        dim_name: str = "MCP_RADIUS",
    ) -> OperationResult:
        """Create a RADIUS dimension on one edge (fillets, arcs, rounds).

        edge_index is the 0-based index from nx_list_edges on the given body.
        """
        return OperationResult(
            **await call(
                "nx_create_radius_dimension",
                {
                    "view_id": view_id,
                    "body_id": body_id,
                    "edge_index": edge_index,
                    "pos_x": pos_x,
                    "pos_y": pos_y,
                    "dim_name": dim_name,
                },
            )
        )

    @mcp.tool()
    async def nx_delete_annotation(annotation_id: str) -> OperationResult:
        """Delete a note / dimension / GDT / roughness annotation.

        Pass either its journal id or name (e.g. "LEN_120", "HANDLE R-5132"), or a
        "kind:index" selector: note:0, dim:1, gdt:0, rough:0 (0-based, in the order
        returned by nx_list_dimensions / nx_list_annotations).
        Useful for cleaning up test annotations that nx_undo cannot reach.
        """
        return OperationResult(
            **await call("nx_delete_annotation", {"annotation_id": annotation_id})
        )

    @mcp.tool()
    async def nx_export_pdf(path: str, sheet_name: str = "") -> OperationResult:
        """Export the current drawing display to a PDF file.

        Pass sheet_name to display that sheet first (the export follows the current
        display). Use nx_list_drawing_sheets to see available sheet names.
        """
        return OperationResult(
            **await call("nx_export_pdf", {"path": resolve_path(path), "sheet_name": sheet_name})
        )

    @mcp.tool()
    async def nx_create_roughness(
        pos_x: float = 20.0,
        pos_y: float = 20.0,
        value: str = "1.6",
    ) -> OperationResult:
        """Create a surface finish (roughness) symbol on the current drawing sheet.

        value goes into the symbol's a1 slot - e.g. "1.6" for Ra 1.6, "3.2" for Ra 3.2.
        Position is in sheet coordinates (mm).
        """
        return OperationResult(
            **await call(
                "nx_create_roughness",
                {"pos_x": pos_x, "pos_y": pos_y, "value": value},
            )
        )

    @mcp.tool()
    async def nx_create_title_block(
        pos_x: float = 0.0,
        pos_y: float = 0.0,
    ) -> OperationResult:
        """Place a title block on the current drawing sheet.

        Position is in sheet coordinates (mm); typically bottom-right, e.g.
        pos_x=340, pos_y=5 for an A3 landscape sheet.
        """
        return OperationResult(
            **await call(
                "nx_create_title_block", {"pos_x": pos_x, "pos_y": pos_y}
            )
        )

    @mcp.tool()
    async def nx_set_attribute(name: str, value: str) -> OperationResult:
        """Set a string attribute on the part.

        This is how a title block gets its CONTENT: title-block cells usually
        reference part attributes. Typical names: 名称 / 图号 / 材料 / 设计 /
        审核 / 比例 / 单位. Value may contain spaces.
        """
        return OperationResult(
            **await call("nx_set_attribute", {"name": name, "value": value})
        )

    @mcp.tool()
    async def nx_list_attributes() -> OperationResult:
        """Read back all user attributes set on the part (title-block content)."""
        return OperationResult(**await call("nx_list_attributes", {}))

    @mcp.tool()
    async def nx_measure_distance(
        body_id: str = "",
        edge_index_1: int = 0,
        edge_index_2: int = 0,
    ) -> OperationResult:
        """Measure the minimum 3D distance between two edges of a body.

        This is a QUERY: no annotation is created. It returns the numeric distance
        (mm) plus the two closest points, so an agent can verify geometry instead of
        guessing. Indices are 0-based from nx_list_edges.
        """
        return OperationResult(
            **await call(
                "nx_measure_distance",
                {
                    "body_id": body_id,
                    "edge_index_1": edge_index_1,
                    "edge_index_2": edge_index_2,
                },
            )
        )

    @mcp.tool()
    async def nx_create_note(
        text: str,
        pos_x: float = 20.0,
        pos_y: float = 20.0,
    ) -> OperationResult:
        """Add technical-requirement / process text to the current drawing sheet.

        Use "|" to start a new line, e.g. "技术要求|1. 未注倒角 C0.5|2. 调质 28-32HRC".
        Position is in sheet coordinates (mm).
        """
        return OperationResult(
            **await call("nx_create_note", {"text": text, "pos_x": pos_x, "pos_y": pos_y})
        )

    @mcp.tool()
    async def nx_create_diameter_dimension(
        view_id: str = "",
        body_id: str = "",
        edge_index: int = 0,
        pos_x: float = 0.0,
        pos_y: float = 0.0,
        upper_tolerance: float = 0.1,
        lower_tolerance: float = -0.1,
        dim_name: str = "MCP_DIAMETER",
    ) -> OperationResult:
        """Create a DIAMETER dimension on one edge (essential for shafts).

        A linear dimension between two edges cannot express a diameter; use this
        for cylindrical/circular features. edge_index is the 0-based index from
        nx_list_edges on the given body.
        """
        return OperationResult(
            **await call(
                "nx_create_diameter_dimension",
                {
                    "view_id": view_id,
                    "body_id": body_id,
                    "edge_index": edge_index,
                    "pos_x": pos_x,
                    "pos_y": pos_y,
                    "upper_tolerance": upper_tolerance,
                    "lower_tolerance": lower_tolerance,
                    "dim_name": dim_name,
                },
            )
        )

    @mcp.tool()
    async def nx_update_views() -> OperationResult:
        """Force every drafting view on the current sheet to a fresh update.

        Clears the out-of-date ("clock") flag shown in the Part Navigator after a
        drawing or projected view is created or modified.
        """
        return OperationResult(**await call("nx_update_views", {}))

    @mcp.tool()
    async def nx_create_dimension(
        view_id: str = "",
        body_id: str = "",
        first_edge_index: int = 0,
        second_edge_index: int = 0,
        pos_x: float = 0.0,
        pos_y: float = 0.0,
        measurement: Literal["horizontal", "vertical", "parallel"] = "horizontal",
        upper_tolerance: float = 0.1,
        lower_tolerance: float = -0.1,
        dim_name: str = "MCP_DIMENSION",
    ) -> OperationResult:
        """Create an associative linear dimension with a bilateral tolerance.

        Measures the distance between two model edges (selected by their 0-based
        index from nx_list_edges on the given body) inside the drafting view.
        Default tolerance is ±0.1 mm using the BilateralTwoLines style.
        """
        return OperationResult(
            **await call(
                "nx_create_dimension",
                {
                    "view_id": view_id,
                    "body_id": body_id,
                    "first_edge_index": first_edge_index,
                    "second_edge_index": second_edge_index,
                    "pos_x": pos_x,
                    "pos_y": pos_y,
                    "measurement": measurement,
                    "upper_tolerance": upper_tolerance,
                    "lower_tolerance": lower_tolerance,
                    "dim_name": dim_name,
                },
            )
        )

    if enable_experimental:
        from nx_mcp.experimental import add_experimental_tools

        add_experimental_tools(
            mcp,
            call,
            CERTIFIED_TOOL_NAMES,
            workspace,
            enable_journal=enable_journal,
        )

    # Additions remain opt-in until each stage has real NX12 acceptance.
    # The certified legacy tool set and all its existing contracts stay intact.
    import os
    if os.environ.get("NX_MCP_ENABLE_AUTOMATION") == "1":
        from nx_mcp.automation_tools import add_automation_tools

        add_automation_tools(mcp, call, resolve_path)
        from nx_mcp.image_model_tools import add_image_model_tools
        add_image_model_tools(mcp, call, resolve_path)

    return mcp
