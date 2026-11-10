# MCP工具索引

以下77项来自实际 `tools/list` 描述。注册schema只定义接口，不自行证明功能通过；样例验证见 [VALIDATION](VALIDATION.md)。完整嵌套类型、默认值、约束及输出schema见 [tool-catalog.json](tool-catalog.json)。

路径必须是配置工作区的相对路径。对象ID取实际返回；拓扑改变/重开后重新查询，不缓存边索引或Tag为永久标识。

|#|工具|必填参数|可选参数|说明|
|---:|---|---|---|---|
|1|`nx_status`|无|无|Report bridge, NX version, and active-part status.|
|2|`nx_create_part`|`path`: string|`units`: mm/inch = "mm"|Create a part inside the configured workspace.|
|3|`nx_open_part`|`path`: string|无|Open a part from the configured workspace.|
|4|`nx_save_part`|无|无|Save the active work part.|
|5|`nx_close_part`|无|`save`: boolean = true|Close the active work part, optionally saving it first.|
|6|`nx_export_step`|`path`: string|无|Export the active work part as STEP inside the configured workspace.|
|7|`nx_list_sketches`|无|无|List sketches in the active work part.|
|8|`nx_list_bodies`|无|无|List bodies in the active work part.|
|9|`nx_list_features`|无|无|List features in the active work part.|
|10|`nx_list_edges`|`body_id`: string|无|Inspect every edge of a body: index, curve type, endpoints, midpoint, length, bounding box, direction and adjacent face count. Read-only.|
|11|`nx_list_faces`|`body_id`: string|无|Inspect every face of a body: index, tag, face type, centroid, area, normal (planar only) and adjacent edge count. Read-only.|
|12|`nx_create_sketch`|无|`plane`: XY/XZ/YZ = "XY"; `name`: string \| null = null|Create and activate a sketch on a principal datum plane.|
|13|`nx_sketch_line`|`sketch_id`: string; `start`: Point2D; `end`: Point2D|无|Add a line to an explicit sketch reference.|
|14|`nx_sketch_rectangle`|`sketch_id`: string; `corner1`: Point2D; `corner2`: Point2D|无|Add a rectangle to an explicit sketch reference.|
|15|`nx_sketch_circle`|`sketch_id`: string; `center`: Point2D; `diameter`: number|无|Add a circle (center + diameter) to an explicit sketch reference.|
|16|`nx_sketch_arc`|`sketch_id`: string; `center`: Point2D; `radius`: number|`start_angle`: number = 0.0; `end_angle`: number = 90.0|Add an arc (center, radius, start/end degrees) to an explicit sketch reference.|
|17|`nx_finish_sketch`|`sketch_id`: string|无|Deactivate and finish an explicit sketch reference.|
|18|`nx_extrude`|`sketch_id`: string; `distance`: number|`reverse`: boolean = false; `start_offset`: number = 0.0; `operation`: create/subtract = "create"; `target_body_id`: string \| null = null|Extrude a sketch into a new body, or subtract it from an existing target body.|
|19|`nx_hole`|`body_id`: string; `center`: Point2D; `diameter`: number; `depth`: number|`start_offset`: number = 0.0|Create a hole on a target body (circle sketch + boolean subtract).|
|20|`nx_counterbore_hole`|`body_id`: string; `center`: Point2D; `hole_diameter`: number; `hole_depth`: number; `counterbore_diameter`: number; `counterbore_depth`: number|`start_offset`: number = 0.0|Create a coaxial counterbore hole: a smaller deep through hole plus a larger shallow counterbore from the top face. Independent from nx_hole.|
|21|`nx_countersink_hole`|`body_id`: string; `center`: Point2D; `hole_diameter`: number; `hole_depth`: number; `countersink_diameter`: number|`countersink_angle`: number = 90.0; `start_offset`: number = 0.0|Create a coaxial countersink (conical) hole: a smaller deep through cylinder plus a conical countersink from the top face. Independent of nx_hole / nx_counterbore_hole.|
|22|`nx_shell`|`body_id`: string; `thickness`: number; `remove_face_index`: integer|`inward`: boolean = true|Shell (hollow) a body: remove one face as an opening and keep a constant wall thickness. First version removes a single face; inward=True keeps the outer dimensions.|
|23|`nx_edge_blend`|`body_id`: string; `radius`: number|`edge_indices`: array[integer] \| null = null|Create an edge blend (fillet) on edges of a body; default all edges.|
|24|`nx_chamfer`|`body_id`: string; `offset`: number|`edge_indices`: array[integer] \| null = null|Create a symmetric chamfer on edges of a body; default all edges.|
|25|`nx_unite`|`target_body_id`: string; `tool_body_ids`: array[string]|无|Boolean-unite one or more tool bodies into the target body. The target body keeps its id; the tool bodies are consumed and disappear from ``nx_list_bodies`` after the boolean.|
|26|`nx_revolve`|`sketch_id`: string; `axis_start`: Point2D; `axis_end`: Point2D|`angle`: number = 360.0; `reverse`: boolean = false|Revolve a closed sketch section around an in-sketch 2D axis. Creates a new solid only (no unite/subtract). ``axis_start`` and ``axis_end`` are sketch XY coordinates defining the revolution axis; ``angle`` is the sweep angle in degrees (0 < angle <= 360).|
|27|`nx_mirror`|`body_id`: string; `plane`: XY/XZ/YZ|`offset`: number = 0.0|Mirror a body about a datum plane. ``plane`` selects the mirror datum plane (XY/XZ/YZ); ``offset`` shifts that plane along its normal by the given mm value (default 0). The mirrored body is created as an independent body and is NOT united with the source; use ``nx_unite`` if merging is required.|
|28|`nx_linear_pattern`|`body_id`: string; `direction`: X/Y/Z; `count`: integer; `spacing`: number|`reverse`: boolean = false|Pattern a body linearly along X/Y/Z. ``count`` is the total number of instances including the original (count=4 yields 4 bodies); ``spacing`` is the per-instance delta in mm. The original body is kept and new instances are independent bodies (NOT united); use ``nx_unite`` if merging is required.|
|29|`nx_circular_pattern`|`body_id`: string; `axis`: X/Y/Z; `center`: Point3D; `count`: integer; `angle`: number|`reverse`: boolean = false|Pattern a body circularly about an axis through ``center``. ``count`` is the total number of instances including the original (count=4 yields 4 bodies); ``angle`` is the total sweep in degrees (360.0 distributes evenly around a full circle). The original body is kept and new instances are independent bodies (NOT united); use ``nx_unite`` if merging is required.|
|30|`nx_undo`|无|无|Undo the last visible NX MCP operation.|
|31|`nx_fit_view`|无|无|Fit the active modeling view.|
|32|`nx_release`|无|无|Clear the current task state and return NX to normal interaction. With the resident C# Loader backend, the Loader and named pipe remain alive for the entire NX session; nx_release does not unload the plugin or require Alt+F8 to start it again.|
|33|`nx_create_drawing_sheet`|无|`name`: string = "MCP_SHEET_1"; `size`: A0/A1/A2/A3/A4 = "A4"; `projection`: first/third = "first"; `scale`: number = 1.0; `model_view`: string = "Top"|Create a drawing sheet and place a base view of the given model view. The part must already contain geometry for the base view to be meaningful.|
|34|`nx_create_projected_view`|`x`: number; `y`: number|`parent_view_id`: string = ""; `name`: string = ""|Project a view from an existing drafting view at sheet position (x, y). Leave parent_view_id empty to use the first drafting view on the sheet.|
|35|`nx_list_drawing_sheets`|无|无|List the drawing sheets in the active part.|
|36|`nx_list_drafting_views`|无|无|List the drafting views on the current drawing sheet.|
|37|`nx_list_dimensions`|无|无|Read back every dimension in the part: measured size and tolerance. Returns one entry per dimension, formatted as "<journal_id>\|<name>\|size=<value>\|tol=<upper>/<lower>\|<tolerance_type>". Use this to verify dimensions you just created, or to READ an existing NX 2D drawing: sizes and tolerances are recovered from the model, so an agent can understand what a drawing specifies without opening the UI.|
|38|`nx_list_annotations`|无|无|Read the non-dimension annotations of a drawing: notes, GD&T, roughness. Each entry is prefixed by kind: "NOTE\|<journal_id>\|<text>" drafting / technical-requirement text "GDT\|<journal_id>\|<characteristic>\|tol=<value>\|datum=<d1>,<d2>,<d3>" "ROUGH\|<journal_id>" surface finish symbol Use this together with nx_list_dimensions to fully understand an existing NX 2D process drawing.|
|39|`nx_create_radius_dimension`|无|`view_id`: string = ""; `body_id`: string = ""; `edge_index`: integer = 0; `pos_x`: number = 0.0; `pos_y`: number = 0.0; `dim_name`: string = "MCP_RADIUS"|Create a RADIUS dimension on one edge (fillets, arcs, rounds). edge_index is the 0-based index from nx_list_edges on the given body.|
|40|`nx_delete_annotation`|`annotation_id`: string|无|Delete a note / dimension / GDT / roughness annotation. Pass either its journal id or name (e.g. "LEN_120", "HANDLE R-5132"), or a "kind:index" selector: note:0, dim:1, gdt:0, rough:0 (0-based, in the order returned by nx_list_dimensions / nx_list_annotations). Useful for cleaning up test annotations that nx_undo cannot reach.|
|41|`nx_export_pdf`|`path`: string|`sheet_name`: string = ""|Export the current drawing display to a PDF file. Pass sheet_name to display that sheet first (the export follows the current display). Use nx_list_drawing_sheets to see available sheet names.|
|42|`nx_create_roughness`|无|`pos_x`: number = 20.0; `pos_y`: number = 20.0; `value`: string = "1.6"|Create a surface finish (roughness) symbol on the current drawing sheet. value goes into the symbol's a1 slot - e.g. "1.6" for Ra 1.6, "3.2" for Ra 3.2. Position is in sheet coordinates (mm).|
|43|`nx_create_title_block`|无|`pos_x`: number = 0.0; `pos_y`: number = 0.0|Place a title block on the current drawing sheet. Position is in sheet coordinates (mm); typically bottom-right, e.g. pos_x=340, pos_y=5 for an A3 landscape sheet.|
|44|`nx_set_attribute`|`name`: string; `value`: string|无|Set a string attribute on the part. This is how a title block gets its CONTENT: title-block cells usually reference part attributes. Typical names: 名称 / 图号 / 材料 / 设计 / 审核 / 比例 / 单位. Value may contain spaces.|
|45|`nx_list_attributes`|无|无|Read back all user attributes set on the part (title-block content).|
|46|`nx_measure_distance`|无|`body_id`: string = ""; `edge_index_1`: integer = 0; `edge_index_2`: integer = 0|Measure the minimum 3D distance between two edges of a body. This is a QUERY: no annotation is created. It returns the numeric distance (mm) plus the two closest points, so an agent can verify geometry instead of guessing. Indices are 0-based from nx_list_edges.|
|47|`nx_create_note`|`text`: string|`pos_x`: number = 20.0; `pos_y`: number = 20.0|Add technical-requirement / process text to the current drawing sheet. Use "\|" to start a new line, e.g. "技术要求\|1. 未注倒角 C0.5\|2. 调质 28-32HRC". Position is in sheet coordinates (mm).|
|48|`nx_create_diameter_dimension`|无|`view_id`: string = ""; `body_id`: string = ""; `edge_index`: integer = 0; `pos_x`: number = 0.0; `pos_y`: number = 0.0; `upper_tolerance`: number = 0.1; `lower_tolerance`: number = -0.1; `dim_name`: string = "MCP_DIAMETER"|Create a DIAMETER dimension on one edge (essential for shafts). A linear dimension between two edges cannot express a diameter; use this for cylindrical/circular features. edge_index is the 0-based index from nx_list_edges on the given body.|
|49|`nx_update_views`|无|无|Force every drafting view on the current sheet to a fresh update. Clears the out-of-date ("clock") flag shown in the Part Navigator after a drawing or projected view is created or modified.|
|50|`nx_create_dimension`|无|`view_id`: string = ""; `body_id`: string = ""; `first_edge_index`: integer = 0; `second_edge_index`: integer = 0; `pos_x`: number = 0.0; `pos_y`: number = 0.0; `measurement`: horizontal/vertical/parallel = "horizontal"; `upper_tolerance`: number = 0.1; `lower_tolerance`: number = -0.1; `dim_name`: string = "MCP_DIMENSION"|Create an associative linear dimension with a bilateral tolerance. Measures the distance between two model edges (selected by their 0-based index from nx_list_edges on the given body) inside the drafting view. Default tolerance is ±0.1 mm using the BilateralTwoLines style.|
|51|`nx_assembly_create`|`path`: string|无|Create a new independent NX12 assembly part inside the workspace.|
|52|`nx_assembly_add`|`path`: string; `name`: string|`parent_id`: string = "/"; `position`: array[number] \| null = null; `rotation`: array[number] \| null = null|Insert an existing .prt, optionally into a subassembly. Position is mm. Rotation is the NX12 nine-value component orientation matrix in API column order; identity is the default. Returned opaque component IDs are used by later tools; do not substitute display names.|
|53|`nx_assembly_list`|无|无|Read the actual component hierarchy, transforms, and part references.|
|54|`nx_assembly_transform`|`component_id`: string|`position`: array[number] \| null = null; `rotation`: array[number] \| null = null; `relative`: boolean = false|Set or increment component translation/orientation in owning assembly coordinates. Position is mm; rotation is a nine-value rigid NX orientation matrix. Transforms use the component definition in its direct parent prototype. Editing a nested definition affects every instance of that subassembly. List readback uses each occurrence's coordinate_part reference frame; do not reuse root occurrence coordinates as nested local inputs.|
|55|`nx_assembly_remove`|`component_id`: string|无|Remove an assembly occurrence; its source .prt file is retained.|
|56|`nx_assembly_replace`|`component_id`: string; `path`: string|无|Replace an occurrence with another workspace .prt using the NX12 API.|
|57|`nx_assembly_component_geometry`|`component_id`: string|无|Read occurrence face/edge selectors for assembly constraints.|
|58|`nx_assembly_constraint`|`kind`: fix/coaxial/touch/distance/angle; `component_a`: string|`component_b`: string = ""; `face_a`: integer = -1; `face_b`: integer = -1; `value`: number = 0.0; `name`: string = ""; `alignment`: infer/same/opposite = "infer"|Create an NX12 assembly constraint and read its actual solver status. Face indices come from nx_assembly_component_geometry. Distance is mm; angle is degrees. A fix constraint requires only component_a.|
|59|`nx_assembly_constraints`|无|无|Read persistent assembly constraints and their native solver states.|
|60|`nx_assembly_delete_constraint`|`constraint_id`: string|无|Delete an explicit root-assembly constraint before repositioning its components. Obtain the current identifier from nx_assembly_constraints. Source prototype geometry and other constraints remain outside this operation.|
|61|`nx_assembly_solve`|无|无|Solve the active assembly constraint network and read resulting positions.|
|62|`nx_assembly_save`|无|无|Save the root and workspace-owned subassemblies with checked load/save results. Files outside the configured workspace must never be written. The Loader validates every referenced prototype before performing saves.|
|63|`nx_assembly_interference`|无|`component_ids`: array[string] \| null = null; `include_touching`: boolean = false|Check actual component-pair interference; report native outcomes.|
|64|`nx_drafting_add_model_view`|`x`: number; `y`: number|`model_view`: string = "Isometric"; `scale`: number = 1.0; `name`: string = ""|Place an additional named model view, including an isometric view.|
|65|`nx_drafting_create_table`|`x`: number; `y`: number; `column_widths`: array[number]; `row_heights`: array[number]; `cells`: array[array[string]]|`name`: string = ""; `text_height`: number = 3.0; `font`: string = "chinesef"|Create an editable NX tabular note, for title blocks or BOM tables. Dimensions and position are sheet mm. Text is explicit; the tool does not invent material, drawing numbers, or standard-compliance claims.|
|66|`nx_drafting_update_table`|`table_id`: string; `updates`: array[TableCellUpdate]|无|Update explicit editable drawing-table cells by zero-based row/column.|
|67|`nx_drafting_list_tables`|无|无|Read actual drawing table cells, dimensions, and identifiers.|
|68|`nx_drafting_center_mark`|`edge_index`: integer|`view_id`: string = "0"; `body_id`: string = "BODY_1"; `size`: number = 5.0|Create a center mark on a circular model edge in the selected view.|
|69|`nx_drafting_centerline`|`edge_index_1`: integer; `edge_index_2`: integer|`view_id`: string = "0"; `body_id`: string = "BODY_1"; `size`: number = 5.0|Create a centerline from two explicit model-edge references.|
|70|`nx_drafting_detail_view`|`view_id`: string; `min_x`: number; `min_y`: number; `max_x`: number; `max_y`: number; `x`: number; `y`: number|`scale`: number = 2.0; `name`: string = ""|Create a native detail view from an explicit sheet-coordinate region.|
|71|`nx_drafting_section_view`|`view_id`: string; `cut_x`: number; `cut_y`: number; `x`: number; `y`: number|`cut_direction`: vertical/horizontal = "vertical"; `scale`: number = 1.0; `name`: string = ""|Create a native section view from a stated cutting-line point/direction.|
|72|`nx_drafting_gdt`|`x`: number; `y`: number|`lines`: array[string] \| null = null; `characteristic`: flatness/straightness/circularity/cylindricity \| null = null; `tolerance`: number \| null = null|Create a native GD&T annotation from explicit NX-formatted tolerance text. Text and datum interpretation must be reviewed against the supplied drawing; creating a symbol does not infer a manufacturing requirement.|
|73|`nx_drafting_balloon`|`x`: number; `y`: number|`text`: string = "1"; `leader_x`: number \| null = null; `leader_y`: number \| null = null|Create a native parts-list balloon with optional explicit leader endpoint.|
|74|`nx_drafting_export_cad`|`path`: string|`sheet_name`: string = ""; `format`: dxf/dwg = "dxf"|Export a sheet through the NX12 translator and verify the output file.|
|75|`nx_drawing_plan`|`source_image`: string; `family`: stepped_shaft/flange/bracket/box/gear; `dimensions`: object|`units`: mm = "mm"; `material`: string = ""|Plan a part from an image and explicitly interpreted dimensions; does not perform OCR. No NX mutation occurs. Missing dimensions return needs_dimensions, and unexpected requirements require review. The image hash binds provenance.|
|76|`nx_model_check`|`plan`: object|无|Read native geometry and check a matching image-plan part; no model mutation.|
|77|`nx_model_from_plan`|`plan`: object; `path`: string|`export_step`: boolean = false|Build a new independent NX12 part using existing MCP bridge commands. Supports explicit stepped shafts, plain flanges, prismatic L brackets, and uniform open-top boxes. Explicitly accepted tolerance-bounded polygonal gears are supported; manufacturing-grade gears are not. Every mutating command checks the active part; partial failures retain a trace.|

## 已验收的调用约定

- `nx_create_part` 的 `units=inch` 会传到原生单位；几何查询按mm归一化。
- `nx_create_sketch(name=...)` 使用真实名称，但继续编辑时使用返回的opaque sketch ID。
- `nx_list_annotations` 返回的 `ANNTAG` selector绑定当前Loader会话和部件；`nx_delete_annotation`拒绝歧义引用。
- `nx_create_title_block` 需要已打开的Drafting sheet；先设明确属性，缺值留空。
- `nx_open_part` 的AP214路径生成新 `__imported.prt`，拒绝覆盖。
- `nx_drafting_export_cad` 的format只有dxf/dwg；实际第三方CAD回读范围见验收摘要。
- `nx_drawing_plan`只接受明确解释的尺寸，不自动OCR。缺尺寸返回needs_dimensions。

## 最小调用示例

```json
[
  {
    "name": "nx_create_part",
    "arguments": {
      "path": "smoke/box.prt",
      "units": "mm"
    }
  },
  {
    "name": "nx_create_sketch",
    "arguments": {
      "plane": "XY",
      "name": "SMOKE_BOX"
    }
  }
]
```

后续rectangle/finish/extrude须使用第二条实际返回的sketch ID；不要仅复制一串调用而忽略前一步错误。
