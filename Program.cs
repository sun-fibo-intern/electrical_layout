using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using Autodesk.AutoCAD.Runtime;
using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcColor = Autodesk.AutoCAD.Colors.Color;
using AcColorMethod = Autodesk.AutoCAD.Colors.ColorMethod;
using AcLine = Autodesk.AutoCAD.DatabaseServices.Line;

[assembly: CommandClass(typeof(IntegratedCadProject))]

public class IntegratedCadProject
{
    static IntegratedCadProject()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (s, a) =>
        {
            string folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string dll = Path.Combine(folder, new AssemblyName(a.Name).Name + ".dll");
            return File.Exists(dll) ? Assembly.LoadFrom(dll) : null;
        };
    }

    class Circuit
    {
        public string No = "";
        public string Room = "";
        public short Color;
    }

    class LoadItem
    {
        public string Code = "";
        public int Watt;
        public int Count;
    }

    class EntityInfo
    {
        public string EntityTypeName = "";
        public string DisplayName = "";
        public string BlockName = "";
        public bool IsBlockRef;

        public string UniqueKey
        {
            get
            {
                if (IsBlockRef)
                    return EntityTypeName + ":" + BlockName.ToUpperInvariant();

                return EntityTypeName;
            }
        }
    }

    [CommandMethod("RUNALLTASKS")]
    public void RunAllTasks()
    {
        CountConnectedWires();
        CountEntities();
        CreateGraph();
    }

    [CommandMethod("CountConnectedWires")]
    public void CountConnectedWires()
    {
        Document doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;

        Editor ed = doc.Editor;
        Database db = doc.Database;

        SelectionSet wireSet = GetWireSelectionSet(ed);
        if (wireSet == null) return;

        PromptEntityResult entRes = ed.GetEntity("\nSelect object to check connections: ");
        if (entRes.Status != PromptStatus.OK) return;

        List<Point3d> connectionPoints = new List<Point3d>();

        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            Entity target = tr.GetObject(entRes.ObjectId, OpenMode.ForRead) as Entity;
            if (target == null) return;

            foreach (SelectedObject obj in wireSet)
            {
                if (obj == null) continue;
                if (obj.ObjectId == entRes.ObjectId) continue;

                Entity wire = tr.GetObject(obj.ObjectId, OpenMode.ForRead) as Entity;
                if (wire == null) continue;
                if (!(wire is Curve)) continue;

                AddConnectionPoints(wire, target, connectionPoints);
            }

            tr.Commit();
        }

        ed.WriteMessage("\nConnected wires count = " + connectionPoints.Count);
    }

    SelectionSet GetWireSelectionSet(Editor ed)
    {
        PromptPointResult p1 = ed.GetPoint("\nSpecify first corner: ");
        if (p1.Status != PromptStatus.OK) return null;

        PromptCornerOptions p2opt = new PromptCornerOptions("\nSpecify opposite corner: ", p1.Value);
        PromptPointResult p2 = ed.GetCorner(p2opt);

        if (p2.Status != PromptStatus.OK) return null;

        PromptSelectionResult res = ed.SelectCrossingWindow(p1.Value, p2.Value);

        if (res.Status != PromptStatus.OK)
        {
            ed.WriteMessage("\nNo objects found.");
            return null;
        }

        ed.WriteMessage("\nTotal objects selected = " + res.Value.Count);
        return res.Value;
    }

    void AddConnectionPoints(Entity wire, Entity target, List<Point3d> points)
    {
        Point3dCollection pts = new Point3dCollection();

        try
        {
            wire.IntersectWith(target, Intersect.OnBothOperands, pts, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            return;
        }

        foreach (Point3d p in pts)
        {
            if (!ContainsPoint(points, p))
                points.Add(p);
        }
    }

    bool ContainsPoint(List<Point3d> points, Point3d point)
    {
        const double tol = 0.001;

        foreach (Point3d p in points)
        {
            if (p.DistanceTo(point) <= tol)
                return true;
        }

        return false;
    }

    [CommandMethod("CountEntities")]
    public void CountEntities()
    {
        Document doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;

        Editor ed = doc.Editor;
        Database db = doc.Database;

        ed.WriteMessage("\n--- PARAMETRIC MULTI ENTITY COUNTER ---");

        PromptSelectionOptions refOpts = new PromptSelectionOptions();
        refOpts.MessageForAdding = "\nSelect reference entities multiple: ";

        PromptSelectionResult refRes = ed.GetSelection(refOpts);
        if (refRes.Status != PromptStatus.OK)
        {
            ed.WriteMessage("\nNo reference objects selected.");
            return;
        }

        PromptPointResult p1 = ed.GetPoint("\nPick first corner of selection window: ");
        if (p1.Status != PromptStatus.OK) return;

        PromptCornerOptions pco = new PromptCornerOptions("\nPick opposite corner: ", p1.Value);
        PromptPointResult p2 = ed.GetCorner(pco);
        if (p2.Status != PromptStatus.OK) return;

        PromptSelectionResult areaRes = ed.SelectWindow(p1.Value, p2.Value);
        if (areaRes.Status != PromptStatus.OK)
        {
            ed.WriteMessage("\nNo objects selected in window.");
            return;
        }

        PromptPointResult tablePt = ed.GetPoint("\nClick table insertion point: ");
        if (tablePt.Status != PromptStatus.OK) return;

        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            try
            {
                Dictionary<string, EntityInfo> targets = new Dictionary<string, EntityInfo>();

                foreach (SelectedObject so in refRes.Value)
                {
                    if (so == null) continue;

                    Entity ent = tr.GetObject(so.ObjectId, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    EntityInfo info = IdentifyEntity(ent);

                    if (!targets.ContainsKey(info.UniqueKey))
                        targets.Add(info.UniqueKey, info);
                }

                Dictionary<string, int> counts = new Dictionary<string, int>();

                foreach (string key in targets.Keys)
                    counts[key] = 0;

                foreach (SelectedObject so in areaRes.Value)
                {
                    if (so == null) continue;

                    Entity ent = tr.GetObject(so.ObjectId, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    string key = GetEntityKey(ent);

                    if (counts.ContainsKey(key))
                        counts[key]++;
                }

                CreateMultiTable(db, tr, targets.Values.ToList(), counts, tablePt.Value);

                tr.Commit();
                ed.WriteMessage("\nTable created successfully.");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nError: " + ex.Message);
                tr.Abort();
            }
        }
    }

    EntityInfo IdentifyEntity(Entity ent)
    {
        if (ent is BlockReference)
        {
            BlockReference br = ent as BlockReference;
            string name = GetBlockName(br);

            return new EntityInfo
            {
                EntityTypeName = nameof(BlockReference),
                DisplayName = "Block: " + name,
                BlockName = name,
                IsBlockRef = true
            };
        }

        return new EntityInfo
        {
            EntityTypeName = ent.GetType().Name,
            DisplayName = ent.GetType().Name,
            IsBlockRef = false
        };
    }

    string GetEntityKey(Entity ent)
    {
        if (ent is BlockReference)
        {
            BlockReference br = ent as BlockReference;
            string name = GetBlockName(br);
            return nameof(BlockReference) + ":" + name.ToUpperInvariant();
        }

        return ent.GetType().Name;
    }

    string GetBlockName(BlockReference br)
    {
        try
        {
            if (br.IsDynamicBlock)
            {
                BlockTableRecord btr =
                    (BlockTableRecord)br.DynamicBlockTableRecord.GetObject(OpenMode.ForRead);

                return btr.Name;
            }
        }
        catch { }

        return br.Name;
    }

    void CreateMultiTable(Database db, Transaction tr,
        List<EntityInfo> entities, Dictionary<string, int> counts, Point3d pt)
    {
        BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        BlockTableRecord ms =
            (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

        int rows = entities.Count + 2;

        Table table = new Table();
        table.TableStyle = db.Tablestyle;
        table.SetSize(rows, 2);
        table.SetColumnWidth(60);
        table.SetRowHeight(12);
        table.Position = pt;

        table.Cells[0, 0].TextString = "ENTITY COUNT TABLE";
        table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
        table.Cells[0, 0].TextHeight = 4;
        table.MergeCells(CellRange.Create(table, 0, 0, 0, 1));

        table.Cells[1, 0].TextString = "ENTITY";
        table.Cells[1, 1].TextString = "COUNT";
        table.Cells[1, 0].Alignment = CellAlignment.MiddleCenter;
        table.Cells[1, 1].Alignment = CellAlignment.MiddleCenter;

        for (int i = 0; i < entities.Count; i++)
        {
            EntityInfo ent = entities[i];
            int row = i + 2;

            table.Cells[row, 0].TextString = ent.DisplayName;
            table.Cells[row, 1].TextString = counts[ent.UniqueKey].ToString();

            table.Cells[row, 0].Alignment = CellAlignment.MiddleLeft;
            table.Cells[row, 1].Alignment = CellAlignment.MiddleCenter;
        }

        table.GenerateLayout();

        ms.AppendEntity(table);
        tr.AddNewlyCreatedDBObject(table, true);
    }

    [CommandMethod("GRAPH")]
    public void CreateGraph()
    {
        Document doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;

        Database db = doc.Database;
        Editor ed = doc.Editor;

        PromptOpenFileOptions opt = new PromptOpenFileOptions("\nSelect Excel file: ");
        opt.Filter = "Excel Files (*.xlsx)|*.xlsx";

        PromptFileNameResult res = ed.GetFileNameForOpen(opt);
        if (res.Status != PromptStatus.OK) return;

        string file = res.StringResult;

        if (!File.Exists(file))
        {
            ed.WriteMessage("\nExcel file not found.");
            return;
        }

        Dictionary<string, List<LoadItem>> loadTable = ReadLoadData(file);
        string flatType = "";

        using (XLWorkbook wb = new XLWorkbook(file))
        {
            IXLWorksheet ws = wb.Worksheet(1);

            flatType = ws.Cell("A1").GetString().Trim();

            if (string.IsNullOrWhiteSpace(flatType))
                flatType = "FLAT TYPE";
        }
        List<Circuit> data = new List<Circuit>
        {
            new Circuit{No="R1", Room="Living / Kitchen Lighting", Color=1},
            new Circuit{No="R2", Room="Kitchen Socket", Color=1},
            new Circuit{No="R3", Room="Living AC", Color=1},
            new Circuit{No="R4", Room="Refrigerator", Color=1},

            new Circuit{No="Y1", Room="C.Bedroom / C.Toilet Lighting", Color=2},
            new Circuit{No="Y2", Room="C.Bedroom AC", Color=2},
            new Circuit{No="Y3", Room="C.Toilet Geyser", Color=2},
            new Circuit{No="Y4", Room="Micro Wave / Mixer", Color=2},

            new Circuit{No="B1", Room="M.Bedroom / M.Toilet Lighting", Color=5},
            new Circuit{No="B2", Room="M.Bedroom AC", Color=5},
            new Circuit{No="B3", Room="M.Toilet Geyser", Color=5},
            new Circuit{No="B4", Room="Spare", Color=5}
        };

        using (doc.LockDocument())
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord ms =
                (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            CreateLayer(db, tr, "SLD_RED", 1);
            CreateLayer(db, tr, "SLD_YELLOW", 2);
            CreateLayer(db, tr, "SLD_BLUE", 5);
            CreateLayer(db, tr, "SLD_TEXT", 7);
            CreateLayer(db, tr, "SLD_FRAME", 8);
            CreateLayer(db, tr, "SLD_GREEN", 3);
            CreateLayer(db, tr, "SLD_MAGENTA", 6);

            Text(ms, tr,flatType,210, 530, 18,"SLD_TEXT");

            DrawMainPanel(ms, tr);
            DrawAllCircuits(ms, tr, data, loadTable);

            tr.Commit();
        }

        ed.WriteMessage("\nGraph created successfully.");
    }

    void DrawMainPanel(BlockTableRecord ms, Transaction tr)
    {
        Rect(ms, tr, 170, 430, 560, 500, "SLD_BLUE");

        Text(ms, tr, "SAMADHAN GORAI", 175, 490, 3.2, "SLD_TEXT");
        Text(ms, tr, "3BHK FLAT TYPE-1", 175, 482, 3.2, "SLD_TEXT");
        Text(ms, tr, "6 WAY TPN DB", 175, 474, 3.2, "SLD_TEXT");

        Text(ms, tr, "MAIN DB", 340, 488, 4.2, "SLD_TEXT");
        Text(ms, tr, "25A 4P MCB", 335, 475, 3.2, "SLD_TEXT");
        Text(ms, tr, "25A 4P 30mA RCCB", 328, 465, 3.2, "SLD_TEXT");

        Text(ms, tr, "R PHASE : 1.5KW", 510, 490, 3.0, "SLD_TEXT");
        Text(ms, tr, "Y PHASE : 1.5KW", 510, 482, 3.0, "SLD_TEXT");
        Text(ms, tr, "B PHASE : 1.5KW", 510, 474, 3.0, "SLD_TEXT");

        double[] xs = { 230, 242, 254, 266, 290, 302, 314, 326, 370, 382, 394, 406 };
        string[] nos = { "R1", "R2", "R3", "R4", "Y1", "Y2", "Y3", "Y4", "B1", "B2", "B3", "B4" };

        for (int i = 0; i < xs.Length; i++)
        {
            string layer = i < 4 ? "SLD_RED" : i < 8 ? "SLD_YELLOW" : "SLD_BLUE";
            Text(ms, tr, nos[i], xs[i] - 3, 445, 3.2, layer);
            Line(ms, tr, xs[i], 430, xs[i], 420, layer);
        }
    }

    void DrawAllCircuits(BlockTableRecord ms, Transaction tr, List<Circuit> data,
        Dictionary<string, List<LoadItem>> loadTable)
    {
        double topY = 430;

        Dictionary<string, double> xMap = new Dictionary<string, double>
        {
            {"R1",230}, {"R2",242}, {"R3",254}, {"R4",266},
            {"Y1",290}, {"Y2",302}, {"Y3",314}, {"Y4",326},
            {"B1",370}, {"B2",382}, {"B3",394}, {"B4",406}
        };

        double leftY = 390;
        double midY = 0;
        double rightY = 390;
        double smallGap = 10;

        foreach (Circuit c in data)
        {
            double x = xMap[c.No];
            string layer = GetLayer(c.Color);

            bool isR = c.No.StartsWith("R");
            bool isY = c.No.StartsWith("Y");
            bool isB = c.No.StartsWith("B");

            if (isY && midY == 0)
                midY = leftY - 15;

            double y = isR ? leftY : isY ? midY : rightY;

            if (c.No == "B1") y = 250;
            else if (c.No == "B2") y = 285;
            else if (c.No == "B3") y = 320;
            else if (c.No == "B4") y = 355;

            List<LoadItem> circuitLoads = null;

            if (!loadTable.TryGetValue(c.No, out circuitLoads))
                circuitLoads = null;

            int totalBranches = 1;

            if (circuitLoads != null && circuitLoads.Count > 0)
                totalBranches = circuitLoads.Sum(a => Math.Min(15, Math.Max(1, a.Count)));

            double lastEntityY =
                y - ((totalBranches - 1) * 7.0) -
                ((circuitLoads == null ? 1 : circuitLoads.Count) * 12.0);

            Line(ms, tr, x, topY, x, lastEntityY, layer);
            Circle(ms, tr, x, y, 1.6, layer);

            if (isR || isY)
            {
                double textY = y - ((totalBranches * 3.5) / 2.0);

                if (circuitLoads != null && circuitLoads.Count > 0)
                    DrawEntities(ms, tr, circuitLoads, x - 10, y, x, layer, true);

                Text(ms, tr, c.Room, x - 170, textY - 2, 3.5, "SLD_TEXT");
            }

            if (isB)
            {
                double textY = y - ((totalBranches * 3.5) / 2.0);

                if (circuitLoads != null && circuitLoads.Count > 0)
                    DrawEntities(ms, tr, circuitLoads, x + 25, y, x, layer, false);

                Text(ms, tr, c.Room, x + 120, textY - 2, 3.5, "SLD_TEXT");
            }

            double used =
                totalBranches * 7.0 +
                ((circuitLoads == null ? 1 : circuitLoads.Count) * 12.0) +
                smallGap + 10.0;

            if (c.No == "R4") used += 40;
            if (c.No == "Y4") used += 40;

            if (isR) leftY -= used;
            if (isY) midY -= used;
            if (isB) rightY -= used;
        }
    }

    void DrawEntities(BlockTableRecord ms, Transaction tr, List<LoadItem> loads,
        double textX, double baseY, double busX, string layer, bool leftSide)
    {
        if (loads == null || loads.Count == 0)
            return;

        double y = baseY - 8;
        double lineLength = 18;
        double branchLength = 10;
        double branchGap = 7.0;
        double entityGap = 12.0;

        foreach (LoadItem item in loads)
        {
            int branchCount = item.Count;

            if (branchCount < 1) branchCount = 1;
            if (branchCount > 15) branchCount = 15;

            double endX = leftSide ? busX - lineLength : busX + lineLength;

            Line(ms, tr, busX, y + 1.2, endX, y + 1.2, layer);
            Circle(ms, tr, busX, y + 1.2, 1.1, layer);

            double branchX, symbolX, labelX;

            if (leftSide)
            {
                branchX = endX + branchLength;
                symbolX = branchX - 12;
                labelX = symbolX - 30;
            }
            else
            {
                branchX = endX - branchLength;
                symbolX = branchX + 12;
                labelX = symbolX + 6;
            }

            double firstY = y + 1.2;
            double lastY = y - ((branchCount - 1) * branchGap) + 1.2;

            Line(ms, tr, branchX, firstY, branchX, lastY, layer);

            for (int b = 0; b < branchCount; b++)
            {
                double by = y - (b * branchGap) + 1.2;

                Line(ms, tr, endX, by, branchX, by, layer);
                DrawEntitySymbol(ms, tr, item.Code, symbolX, by, leftSide);

                string name = item.Code;
                if (name == "TL") name = "Tube Light";

                Text(ms, tr, name + " " + (b + 1), labelX, by - 1.3, 2.5, "SLD_GREEN");
            }

            y -= (branchCount * branchGap) + entityGap;
        }
    }

    void DrawEntitySymbol(BlockTableRecord ms, Transaction tr, string code,
        double x, double y, bool leftSide)
    {
        string symbolLayer = "SLD_MAGENTA";
        string redLayer = "SLD_RED";
        string blueLayer = "SLD_BLUE";

        if (leftSide)
            Line(ms, tr, x + 4, y - 1, x + 8, y + 1, symbolLayer);
        else
            Line(ms, tr, x - 4, y - 1, x - 8, y + 1, symbolLayer);

        if (code == "SO" || code == "CHG" || code == "CTV" || code == "SS")
        {
            if (leftSide)
            {
                Line(ms, tr, x + 4, y - 1, x + 8, y + 1, symbolLayer);
                Line(ms, tr, x + 10, y - 1, x + 14, y + 1, symbolLayer);
            }
            else
            {
                Line(ms, tr, x - 4, y - 1, x - 8, y + 1, symbolLayer);
                Line(ms, tr, x - 10, y - 1, x - 14, y + 1, symbolLayer);
            }
        }

        if (code == "TL" || code == "BL" || code == "CL")
        {
            Circle(ms, tr, x, y, 2.0, redLayer);
            Line(ms, tr, x - 2.8, y, x + 2.8, y, redLayer);
            Line(ms, tr, x, y - 2.8, x, y + 2.8, redLayer);
        }
        else if (code == "CF")
        {
            Circle(ms, tr, x, y, 1.2, blueLayer);
            Line(ms, tr, x, y, x + 4, y + 2, blueLayer);
            Line(ms, tr, x, y, x - 4, y + 2, blueLayer);
            Line(ms, tr, x, y, x, y - 4, blueLayer);
        }
        else if (code == "SO" || code == "CHG" || code == "CTV" || code == "SS")
        {
            Rect(ms, tr, x - 2.2, y - 1.6, x + 2.2, y + 1.6, redLayer);
            Circle(ms, tr, x - 0.8, y, 0.3, redLayer);
            Circle(ms, tr, x + 0.8, y, 0.3, redLayer);
        }
        else if (code == "AC")
        {
            Rect(ms, tr, x - 4, y - 1.6, x + 4, y + 1.6, blueLayer);
            Line(ms, tr, x - 2.5, y - 2.5, x + 2.5, y - 2.5, blueLayer);
        }
        else if (code == "GY")
        {
            Circle(ms, tr, x, y, 2.3, redLayer);
            Line(ms, tr, x - 1.5, y + 2.3, x + 1.5, y + 2.3, redLayer);
        }
        else if (code == "REF" || code == "WM" || code == "WP" || code == "MX" || code == "MW")
        {
            Rect(ms, tr, x - 2.5, y - 3, x + 2.5, y + 3, blueLayer);
            Line(ms, tr, x - 1.5, y, x + 1.5, y, blueLayer);
        }
        else
        {
            Circle(ms, tr, x, y, 1.8, symbolLayer);
        }
    }

    Dictionary<string, List<LoadItem>> ReadLoadData(string path)
    {
        Dictionary<string, List<LoadItem>> dict = new Dictionary<string, List<LoadItem>>();

        using (XLWorkbook wb = new XLWorkbook(path))
        {
            IXLWorksheet ws = wb.Worksheets.First();

            int headerRow = 8;
            int wattRow = 9;

            for (int r = 10; r <= 30; r++)
            {
                string refNo = ws.Cell(r, 4).GetString().Trim();

                if (string.IsNullOrWhiteSpace(refNo)) continue;
                if (!(refNo.StartsWith("R") || refNo.StartsWith("Y") || refNo.StartsWith("B"))) continue;

                List<LoadItem> list = new List<LoadItem>();

                for (int c = 9; c <= 28; c++)
                {
                    string header = ws.Cell(headerRow, c).GetString().Trim();

                    if (string.IsNullOrWhiteSpace(header))
                        header = ws.Cell(7, c).GetString().Trim();

                    string code = MapHeaderToCode(header);
                    int watt = ReadInt(ws.Cell(wattRow, c));
                    int count = ReadInt(ws.Cell(r, c));

                    if (count > 0 && watt > 0 && code != "")
                    {
                        list.Add(new LoadItem
                        {
                            Code = code,
                            Watt = watt,
                            Count = count
                        });
                    }
                }

                if (list.Count > 0)
                    dict[refNo] = list;
            }
        }

        return dict;
    }

    int ReadInt(IXLCell cell)
    {
        try
        {
            return (int)Math.Round(cell.GetDouble());
        }
        catch
        {
            string s = cell.GetString();
            string clean = new string(s.Where(ch => char.IsDigit(ch) || ch == '.').ToArray());

            if (double.TryParse(clean, out double d))
                return (int)Math.Round(d);

            return 0;
        }
    }

    string MapHeaderToCode(string header)
    {
        if (string.IsNullOrWhiteSpace(header)) return "";

        string h = header.ToUpperInvariant();

        if (h.Contains("TUBE")) return "TL";
        if (h.Contains("BRACKET")) return "BL";
        if (h.Contains("CEILING") && h.Contains("FAN")) return "CF";
        if (h.Contains("CEILING") && h.Contains("LIGHT")) return "CL";
        if (h.Contains("CHANDEL")) return "CL";
        if (h.Contains("BELL")) return "BB";
        if (h.Contains("EXHAUST")) return "EF";
        if (h.Contains("FOOT")) return "FL";
        if (h.Contains("COMPUTER") || h.Contains("TV")) return "CTV";
        if (h.Contains("CHARGER")) return "CHG";
        if (h.Contains("SHAVER")) return "SS";
        if (h.Contains("SOCKET")) return "SO";
        if (h.Contains("WASH")) return "WM";
        if (h.Contains("WATER")) return "WP";
        if (h.Contains("CHIMNEY")) return "CP";
        if (h.Contains("MIXER")) return "MX";
        if (h.Contains("MICRO")) return "MW";
        if (h.Contains("REFRIGE")) return "REF";
        if (h.Contains("AC")) return "AC";
        if (h.Contains("GEYSER")) return "GY";

        return header.Length > 4
            ? header.Substring(0, 4).ToUpperInvariant()
            : header.ToUpperInvariant();
    }

    string GetLayer(short color)
    {
        if (color == 1) return "SLD_RED";
        if (color == 2) return "SLD_YELLOW";
        return "SLD_BLUE";
    }

    void Line(BlockTableRecord ms, Transaction tr,
        double x1, double y1, double x2, double y2, string layer)
    {
        AcLine l = new AcLine(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0));
        l.Layer = layer;

        ms.AppendEntity(l);
        tr.AddNewlyCreatedDBObject(l, true);
    }

    void Circle(BlockTableRecord ms, Transaction tr, double x, double y, double r, string layer)
    {
        Circle c = new Circle(new Point3d(x, y, 0), Vector3d.ZAxis, r);
        c.Layer = layer;

        ms.AppendEntity(c);
        tr.AddNewlyCreatedDBObject(c, true);
    }

    void Rect(BlockTableRecord ms, Transaction tr,
        double x1, double y1, double x2, double y2, string layer)
    {
        Line(ms, tr, x1, y1, x2, y1, layer);
        Line(ms, tr, x2, y1, x2, y2, layer);
        Line(ms, tr, x2, y2, x1, y2, layer);
        Line(ms, tr, x1, y2, x1, y1, layer);
    }

    void Text(BlockTableRecord ms, Transaction tr,
        string value, double x, double y, double h, string layer)
    {
        DBText t = new DBText();
        t.Position = new Point3d(x, y, 0);
        t.TextString = value ?? "";
        t.Height = h;
        t.Layer = layer;

        ms.AppendEntity(t);
        tr.AddNewlyCreatedDBObject(t, true);
    }

    void CreateLayer(Database db, Transaction tr, string name, short color)
    {
        LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

        if (!lt.Has(name))
        {
            lt.UpgradeOpen();

            LayerTableRecord ltr = new LayerTableRecord();
            ltr.Name = name;
            ltr.Color = AcColor.FromColorIndex(AcColorMethod.ByAci, color);

            lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
        }
    }
}