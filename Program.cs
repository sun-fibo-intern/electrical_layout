using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
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
        AppDomain.CurrentDomain.AssemblyResolve += (s, a) => {
            string? folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); if (string.IsNullOrWhiteSpace(folder)) return null;

            AssemblyName? requested = new AssemblyName(a.Name);
            string dll = Path.Combine(folder, requested.Name + ".dll");

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

    class WireAnalysisResult
    {
        public int ConnectionCount;
        public List<Point3d> ConnectionPoints = new List<Point3d>();
    }

    class EntityCountResult
    {
        public int TotalCount;
        public List<EntityInfo> Entities = new List<EntityInfo>();
        public Dictionary<string, int> Counts = new Dictionary<string, int>();
        public int ConnectionCount;
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
        WireAnalysisResult wireAnalysis = AnalyzeConnectedWires();
        if (wireAnalysis == null)
            return;

        EntityCountResult entityCount = CountEntities(wireAnalysis);
        if (entityCount == null)
            return;

        CreateGraph(entityCount);
    }

    [CommandMethod("CountConnectedWires")]
    public void CountConnectedWires()
    {
        AnalyzeConnectedWires();
    }

    WireAnalysisResult AnalyzeConnectedWires()
    {
        Document doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return null;

        Editor ed = doc.Editor;
        Database db = doc.Database;

        SelectionSet wireSet = GetWireSelectionSet(ed);
        if (wireSet == null) return null;

        PromptEntityResult entRes = ed.GetEntity("\nSelect object to check connections: ");
        if (entRes.Status != PromptStatus.OK) return null;

        List<Point3d> connectionPoints = new List<Point3d>();

        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            Entity target = tr.GetObject(entRes.ObjectId, OpenMode.ForRead) as Entity;
            if (target == null) return null;

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

        return new WireAnalysisResult
        {
            ConnectionCount = connectionPoints.Count,
            ConnectionPoints = connectionPoints
        };
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

    [CommandMethod("CountEntitie")]
    public void CountEntities()
    {
        CountEntities(null);
    }

    EntityCountResult CountEntities(WireAnalysisResult wireAnalysis)
    {
        Document doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return null;

        Editor ed = doc.Editor;
        Database db = doc.Database;

        ed.WriteMessage("\n--- PARAMETRIC MULTI ENTITY COUNTER ---");

        if (wireAnalysis != null)
            ed.WriteMessage("\nConnections from step 1 = " + wireAnalysis.ConnectionCount);

        PromptSelectionOptions refOpts = new PromptSelectionOptions();
        refOpts.MessageForAdding = "\nSelect reference entities multiple: ";

        PromptSelectionResult refRes = ed.GetSelection(refOpts);
        if (refRes.Status != PromptStatus.OK)
        {
            ed.WriteMessage("\nNo reference objects selected.");
            return null;
        }

        PromptPointResult p1 = ed.GetPoint("\nPick first corner of selection window: ");
        if (p1.Status != PromptStatus.OK) return null;

        PromptCornerOptions pco = new PromptCornerOptions("\nPick opposite corner: ", p1.Value);
        PromptPointResult p2 = ed.GetCorner(pco);
        if (p2.Status != PromptStatus.OK) return null;

        PromptSelectionResult areaRes = ed.SelectWindow(p1.Value, p2.Value);
        if (areaRes.Status != PromptStatus.OK)
        {
            ed.WriteMessage("\nNo objects selected in window.");
            return null;
        }

        PromptPointResult tablePt = ed.GetPoint("\nClick table insertion point: ");
        if (tablePt.Status != PromptStatus.OK) return null;

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

                CreateMultiTable(db, tr, targets.Values.ToList(), counts, tablePt.Value, wireAnalysis);

                tr.Commit();
                ed.WriteMessage("\nTable created successfully.");

                return new EntityCountResult
                {
                    TotalCount = counts.Values.Sum(),
                    Entities = targets.Values.ToList(),
                    Counts = counts,
                    ConnectionCount = wireAnalysis == null ? 0 : wireAnalysis.ConnectionCount
                };
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nError: " + ex.Message);
                tr.Abort();
                return null;
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

    XLWorkbook OpenWorkbookShared(string path)
    {
        FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new XLWorkbook(fs);
    }

    List<string> GetWorksheetNames(string path)
    {
        using (XLWorkbook wb = OpenWorkbookShared(path))
        {
            return wb.Worksheets.Select(ws => ws.Name).ToList();
        }
    }

    string SelectWorksheetName(Editor ed, List<string> worksheetNames)
    {
        if (worksheetNames == null || worksheetNames.Count == 0)
        {
            ed.WriteMessage("\nNo worksheets found in the selected Excel file.");
            return null;
        }

        if (worksheetNames.Count == 1)
        {
            ed.WriteMessage("\nUsing worksheet: " + worksheetNames[0]);
            return worksheetNames[0];
        }

        ed.WriteMessage("\nAvailable worksheets:");
        for (int i = 0; i < worksheetNames.Count; i++)
            ed.WriteMessage("\n  " + (i + 1) + ". " + worksheetNames[i]);

        PromptStringOptions opt = new PromptStringOptions("\nEnter worksheet number or name: ");
        opt.AllowSpaces = true;

        PromptResult res = ed.GetString(opt);
        if (res.Status != PromptStatus.OK)
            return null;

        string input = res.StringResult.Trim();

        if (int.TryParse(input, out int index) && index >= 1 && index <= worksheetNames.Count)
            return worksheetNames[index - 1];

        string match = worksheetNames.FirstOrDefault(x =>
            string.Equals(x, input, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(match))
            return match;

        ed.WriteMessage("\nWorksheet not found: " + input);
        return null;
    }

    void CreateMultiTable(Database db, Transaction tr,
        List<EntityInfo> entities, Dictionary<string, int> counts, Point3d pt, WireAnalysisResult wireAnalysis)
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

        string tableTitle = "ENTITY COUNT TABLE";
        if (wireAnalysis != null)
            tableTitle += " | WIRES=" + wireAnalysis.ConnectionCount;

        table.Cells[0, 0].TextString = tableTitle;
        table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
        table.Cells[0, 0].TextHeight = 6;
        table.MergeCells(CellRange.Create(table, 0, 0, 0, 1));

        table.Cells[1, 0].TextString = "ENTITY";
        table.Cells[1, 1].TextString = "COUNT";
        table.Cells[1, 0].Alignment = CellAlignment.MiddleCenter;
        table.Cells[1, 1].Alignment = CellAlignment.MiddleCenter;
        table.Cells[1, 0].TextHeight = 4.5;
        table.Cells[1, 1].TextHeight = 4.5;

        for (int i = 0; i < entities.Count; i++)
        {
            EntityInfo ent = entities[i];
            int row = i + 2;

            table.Cells[row, 0].TextString = ent.DisplayName;
            table.Cells[row, 1].TextString = counts[ent.UniqueKey].ToString();

            table.Cells[row, 0].Alignment = CellAlignment.MiddleCenter;
            table.Cells[row, 1].Alignment = CellAlignment.MiddleCenter;
            table.Cells[row, 0].TextHeight = 3.8;
            table.Cells[row, 1].TextHeight = 3.8;
        }

        table.GenerateLayout();

        ms.AppendEntity(table);
        tr.AddNewlyCreatedDBObject(table, true);
    }

    [CommandMethod("GRAPH")]
    public void CreateGraph()
    {
        CreateGraph(null);
    }

    void CreateGraph(EntityCountResult entityCount)
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

        List<string> worksheetNames = GetWorksheetNames(file);
        string worksheetName = SelectWorksheetName(ed, worksheetNames);
        if (string.IsNullOrWhiteSpace(worksheetName))
            return;

        Dictionary<string, List<LoadItem>> loadTable = ReadLoadData(file, worksheetName);
        List<Circuit> data = ReadCircuitData(file, worksheetName);
        string flatType = "";

        using (XLWorkbook wb = OpenWorkbookShared(file))
        {
            IXLWorksheet ws = wb.Worksheets.FirstOrDefault(s => string.Equals(s.Name, worksheetName, StringComparison.OrdinalIgnoreCase))
                ?? wb.Worksheets.First();

            flatType = ws.Cell("A1").GetString().Trim();

            if (string.IsNullOrWhiteSpace(flatType))
                flatType = worksheetName;
        }

        if (data.Count == 0)
        {
            ed.WriteMessage("\nNo circuit rows found in the selected worksheet.");
            return;
        }

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

            Text(ms, tr, flatType, 210, 530, 18, "SLD_TEXT");

            if (entityCount != null)
            {
                Text(ms, tr, "COUNTED ENTITY TYPES: " + entityCount.Entities.Count, 210, 520, 4, "SLD_TEXT");
                Text(ms, tr, "CONNECTIONS FROM STEP 1: " + entityCount.ConnectionCount, 210, 512, 4, "SLD_TEXT");
            }

            Dictionary<string, double> phaseLoads = ReadPhaseLoads(file, worksheetName);

            DrawMainPanel(ms, tr, data, flatType, phaseLoads);
            DrawAllCircuits(ms, tr, data, loadTable);

            tr.Commit();
        }

        ed.WriteMessage("\nGraph created successfully.");
    }

    void DrawMainPanel(BlockTableRecord ms, Transaction tr, List<Circuit> data,
        string flatType, Dictionary<string, double> phaseLoads)
    {
        // Draw three sides of the panel (skip left line)
        Line(ms, tr, 170, 500, 560, 500, "SLD_BLUE");  // Top
        Line(ms, tr, 560, 500, 560, 430, "SLD_BLUE");  // Right
        Line(ms, tr, 560, 430, 170, 430, "SLD_BLUE");  // Bottom

        Text(ms, tr, "SAMADHAN GORAI", 175, 490, 3.2, "SLD_TEXT");
        Text(ms, tr, FormatFlatType(flatType), 175, 482, 3.2, "SLD_TEXT");
        Text(ms, tr, FormatDbType(flatType), 175, 474, 3.2, "SLD_TEXT");

        Text(ms, tr, "MAIN DB", 340, 488, 4.2, "SLD_TEXT");
        Text(ms, tr, "25A 4P MCB", 335, 475, 3.2, "SLD_TEXT");
        Text(ms, tr, "25A 4P 30mA RCCB", 328, 465, 3.2, "SLD_TEXT");

        Text(ms, tr, "R PHASE : " + FormatPhaseLoad(phaseLoads, "R"), 510, 490, 3.0, "SLD_TEXT");
        Text(ms, tr, "Y PHASE : " + FormatPhaseLoad(phaseLoads, "Y"), 510, 482, 3.0, "SLD_TEXT");
        Text(ms, tr, "B PHASE : " + FormatPhaseLoad(phaseLoads, "B"), 510, 474, 3.0, "SLD_TEXT");

        double[] xs = { 230, 242, 254, 266, 290, 302, 314, 326, 370, 382, 394, 406 };

        for (int i = 0; i < xs.Length && i < data.Count; i++)
        {
            Circuit c = data[i];
            string layer = GetLayer(c.Color);
            Text(ms, tr, c.No, xs[i] - 3, 445, 3.2, layer);
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
        double rightY = 390;
        double smallGap = 10;
        List<string> sequence = new List<string>
    {
        "R1", "R2", "R3", "R4",
        "B4", "B3", "B2", "B1",
        "Y4", "Y3", "Y2", "Y1"
    };

        foreach (Circuit c in data.OrderBy(c =>
        {
            int index = sequence.IndexOf(c.No);
            return index < 0 ? int.MaxValue : index;
        }))
        {
            if (!xMap.ContainsKey(c.No))
                continue;

            double x = xMap[c.No];
            string layer = GetLayer(c.Color);

            bool isR = c.No.StartsWith("R");
            bool isLeftSide = isR;

            double y = isLeftSide ? leftY : rightY;

            List<LoadItem> circuitLoads = loadTable.TryGetValue(c.No, out List<LoadItem> foundLoads)
                ? foundLoads
                : null;

            int totalBranches = 1;

            if (circuitLoads != null && circuitLoads.Count > 0)
                totalBranches = circuitLoads.Sum(GetBranchCount);

            double lastEntityY =
                y - ((totalBranches - 1) * 7.0) -
                ((circuitLoads == null ? 1 : circuitLoads.Count) * 12.0);

            Line(ms, tr, x, topY, x, lastEntityY, layer);
            Circle(ms, tr, x, y, 1.6, layer);

            if (isLeftSide)
            {
                double textY = y - ((totalBranches * 3.5) / 2.0);

                if (circuitLoads != null && circuitLoads.Count > 0)
                    DrawEntities(ms, tr, circuitLoads, y, x, layer, true);

                Text(ms, tr, c.Room, x - 170, textY - 2, 3.5, "SLD_TEXT");
            }

            if (!isLeftSide)
            {
                double textY = y - ((totalBranches * 3.5) / 2.0);

                if (circuitLoads != null && circuitLoads.Count > 0)
                    DrawEntities(ms, tr, circuitLoads, y, x, layer, false);

                Text(ms, tr, c.Room, x + 120, textY - 2, 3.5, "SLD_TEXT");
            }

            double used =
                totalBranches * 7.0 +
                ((circuitLoads == null ? 1 : circuitLoads.Count) * 12.0) +
                smallGap + 10.0;

            if (c.No == "R4") used += 40;
            if (c.No == "Y4") used += 40;

            if (isLeftSide) leftY -= used;
            if (!isLeftSide) rightY -= used;
        }
    }

    void DrawEntities(BlockTableRecord ms, Transaction tr, List<LoadItem> loads,
        double baseY, double busX, string layer, bool leftSide)
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
            int branchCount = GetBranchCount(item);

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

                double sX = (endX + branchX) / 2.0;

                Line(ms, tr, endX, by, branchX, by, layer);

                DrawSwitchSymbol(ms, tr, sX, by, leftSide, item.Code);

                if (item.Code != "SW")
                    DrawEntitySymbol(ms, tr, item.Code, symbolX, by, leftSide, layer);

                string name = item.Code;
                if (name == "TL") name = "Tube Light";
                if (name == "SW") name = "Switch";
                if (name == "CF") name = "F";

                bool joinNoSpace = name == "BL" || name == "CL" || name == "TL" || name == "F";
                string label = branchCount == 1
                    ? (joinNoSpace ? name + "1" : name)
                    : (joinNoSpace ? name + (b + 1) : name + " " + (b + 1));
                Text(ms, tr, label, labelX, by - 1.3, 2.5, "SLD_GREEN");
            }

            y -= (branchCount * branchGap) + entityGap;
        }
    }

    int GetBranchCount(LoadItem item)
    {
        if (item == null)
            return 1;

        if (item.Code == "SW")
            return 1;

        int count = item.Count;

        if (count < 1) count = 1;
        if (count > 15) count = 15;

        return count;
    }

    void DrawSwitchSymbol(BlockTableRecord ms, Transaction tr, double x, double y,
        bool leftSide, string code)
    {
        string switchLayer = "SLD_MAGENTA";
        int switchCount = GetSwitchCount(code);

        if (switchCount == 2)
        {
            DrawTwoWaySwitchSymbol(ms, tr, x, y, leftSide, switchLayer);
            return;
        }

        double spacing = 2.1;

        for (int i = 0; i < switchCount; i++)
        {
            double dx = (i - (switchCount - 1) / 2.0) * spacing;

            if (leftSide)
            {
                Circle(ms, tr, x + 0.75 + dx, y, 0.22, switchLayer);
                Line(ms, tr, x + 0.75 + dx, y, x - 0.75 + dx, y + 1.95, switchLayer);
            }
            else
            {
                Circle(ms, tr, x - 0.75 - dx, y, 0.22, switchLayer);
                Line(ms, tr, x - 0.75 - dx, y, x + 0.75 - dx, y + 1.95, switchLayer);
            }
        }
    }

    void DrawTwoWaySwitchSymbol(BlockTableRecord ms, Transaction tr, double x, double y,
        bool leftSide, string switchLayer)
    {
        const double armHalfWidth = 1.5;
        const double armHeight = 2.0;
        const double circleRadius = 0.35;

        double apexX = x;
        double apexY = y;
        double leftX = x - armHalfWidth;
        double leftY = y + armHeight;
        double rightX = x + armHalfWidth;
        double rightY = y + armHeight;
        double circleCenterY = y - armHeight * 0.5;

        Line(ms, tr, apexX, apexY, leftX, leftY, switchLayer);
        Line(ms, tr, apexX, apexY, rightX, rightY, switchLayer);
        Line(ms, tr, apexX, apexY, apexX, circleCenterY + circleRadius, switchLayer);
        Circle(ms, tr, apexX, circleCenterY, circleRadius, switchLayer);
    }

    int GetSwitchCount(string code)
    {
        if (code == "SO" || code == "CHG" || code == "CTV" || code == "SS")
            return 2;

        return 1;
    }

    void DrawEntitySymbol(BlockTableRecord ms, Transaction tr, string code,
        double x, double y, bool leftSide, string phaseLayer)
    {
        string accentLayer = "SLD_MAGENTA";
        string detailLayer = "SLD_GREEN";
        string symbolLayer = GetSymbolLayer(code, phaseLayer);

        if (code == "BL")
        {
            Circle(ms, tr, x, y, 1.6, symbolLayer);
            Line(ms, tr, x - 3.2, y, x - 1.6, y, symbolLayer);
            Line(ms, tr, x + 1.6, y, x + 3.2, y, symbolLayer);
            Circle(ms, tr, x, y - 2.4, 0.55, accentLayer);
        }
        else if (code == "CL")
        {
            Circle(ms, tr, x, y, 2.4, symbolLayer);
            Circle(ms, tr, x, y, 1.4, symbolLayer);
            Line(ms, tr, x - 2.4, y, x + 2.4, y, symbolLayer);
            Line(ms, tr, x, y - 2.4, x, y + 2.4, symbolLayer);
        }
        else if (code == "TL")
        {
            Rect(ms, tr, x - 3.2, y - 0.9, x + 3.2, y + 0.9, symbolLayer);
            Line(ms, tr, x - 2.6, y, x + 2.6, y, symbolLayer);
        }
        else if (code == "CF")
        {
            Circle(ms, tr, x, y, 1.2, symbolLayer);
            Line(ms, tr, x, y, x + 4, y + 2, symbolLayer);
            Line(ms, tr, x, y, x - 4, y + 2, symbolLayer);
            Line(ms, tr, x, y, x, y - 4, symbolLayer);
        }
        else if (code == "SO" || code == "CHG" || code == "CTV" || code == "SS")
        {
            Rect(ms, tr, x - 2.2, y - 1.6, x + 2.2, y + 1.6, symbolLayer);
            Circle(ms, tr, x - 0.8, y, 0.3, accentLayer);
            Circle(ms, tr, x + 0.8, y, 0.3, detailLayer);
        }
        else if (code == "AC")
        {
            Rect(ms, tr, x - 4, y - 1.6, x + 4, y + 1.6, symbolLayer);
            Line(ms, tr, x - 2.5, y - 2.5, x + 2.5, y - 2.5, symbolLayer);
        }
        else if (code == "GY")
        {
            Circle(ms, tr, x, y, 2.3, symbolLayer);
            Line(ms, tr, x - 1.5, y + 2.3, x + 1.5, y + 2.3, symbolLayer);
        }
        else if (code == "SW")
        {
            DrawSwitchSymbol(ms, tr, x, y, leftSide, code);
        }
        else if (code == "REF" || code == "WM" || code == "WP" || code == "MX" || code == "MW")
        {
            Rect(ms, tr, x - 2.5, y - 3, x + 2.5, y + 3, symbolLayer);
            Line(ms, tr, x - 1.5, y, x + 1.5, y, symbolLayer);
        }
        else
        {
            Circle(ms, tr, x, y, 1.8, symbolLayer);
        }
    }

    string GetSymbolLayer(string code, string phaseLayer)
    {
        if (code == "BL" || code == "CF" || code == "EF" || code == "FL")
            return "SLD_BLUE";

        if (code == "TL" || code == "CL" || code == "BB")
            return "SLD_RED";

        if (code == "SW")
            return "SLD_MAGENTA";

        return phaseLayer;
    }

    List<Circuit> ReadCircuitData(string path, string worksheetName)
    {
        List<Circuit> circuits = new List<Circuit>();

        using (XLWorkbook wb = OpenWorkbookShared(path))
        {
            IXLWorksheet ws = wb.Worksheets.FirstOrDefault(x =>
                string.Equals(x.Name, worksheetName, StringComparison.OrdinalIgnoreCase))
                ?? wb.Worksheets.First();

            for (int r = 10; r <= 30; r++)
            {
                string refNo = GetAccurateCircuitReference(ws, r);
                string room = ws.Cell(r, 8).GetString().Trim();

                if (string.IsNullOrWhiteSpace(refNo))
                    continue;

                if (!(refNo.StartsWith("R", StringComparison.OrdinalIgnoreCase) ||
                      refNo.StartsWith("Y", StringComparison.OrdinalIgnoreCase) ||
                      refNo.StartsWith("B", StringComparison.OrdinalIgnoreCase)))
                    continue;

                circuits.Add(new Circuit
                {
                    No = refNo,
                    Room = string.IsNullOrWhiteSpace(room) ? refNo : room,
                    Color = refNo.StartsWith("R", StringComparison.OrdinalIgnoreCase) ? (short)1 :
                            refNo.StartsWith("Y", StringComparison.OrdinalIgnoreCase) ? (short)2 : (short)5
                });
            }
        }

        return circuits;
    }

    Dictionary<string, List<LoadItem>> ReadLoadData(string path, string worksheetName)
    {
        Dictionary<string, List<LoadItem>> dict = new Dictionary<string, List<LoadItem>>();

        using (XLWorkbook wb = OpenWorkbookShared(path))
        {
            IXLWorksheet ws = wb.Worksheets.FirstOrDefault(x =>
                string.Equals(x.Name, worksheetName, StringComparison.OrdinalIgnoreCase))
                ?? wb.Worksheets.First();

            int headerRow = 8;
            int wattRow = 9;

            for (int r = 10; r <= 30; r++)
            {
                string refNo = GetAccurateCircuitReference(ws, r);

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
                    int count = ReadPointCount(ws.Cell(r, c), code);

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

    Dictionary<string, double> ReadPhaseLoads(string path, string worksheetName)
    {
        Dictionary<string, double> phaseLoads = new Dictionary<string, double>
    {
        { "R", 0 },
        { "Y", 0 },
        { "B", 0 }
    };

        using (XLWorkbook wb = OpenWorkbookShared(path))
        {
            IXLWorksheet ws = wb.Worksheets.FirstOrDefault(x =>
                string.Equals(x.Name, worksheetName, StringComparison.OrdinalIgnoreCase))
                ?? wb.Worksheets.First();

            for (int r = 10; r <= 30; r++)
            {
                string phase = GetCircuitPhase(ws, r);
                if (string.IsNullOrWhiteSpace(phase))
                    continue;

                double load = ReadDouble(ws.Cell(r, PhaseLoadColumn(phase)));
                phaseLoads[phase] += load;
            }
        }

        return phaseLoads;
    }

    string GetAccurateCircuitReference(IXLWorksheet ws, int row)
    {
        string existing = ws.Cell(row, 4).GetString().Trim().ToUpperInvariant();
        string phase = GetCircuitPhase(ws, row);
        int serial = ReadInt(ws.Cell(row, 3));

        if (!string.IsNullOrWhiteSpace(phase) && serial >= 1 && serial <= 4)
            return phase + serial;

        return existing;
    }

    string GetCircuitPhase(IXLWorksheet ws, int row)
    {
        foreach (string phase in new[] { "R", "Y", "B" })
        {
            if (ReadDouble(ws.Cell(row, PhaseLoadColumn(phase))) > 0)
                return phase;
        }

        string existing = ws.Cell(row, 4).GetString().Trim().ToUpperInvariant();
        if (existing.StartsWith("R")) return "R";
        if (existing.StartsWith("Y")) return "Y";
        if (existing.StartsWith("B")) return "B";

        return "";
    }

    int PhaseLoadColumn(string phase)
    {
        if (phase == "R") return 29;
        if (phase == "Y") return 30;
        return 31;
    }

    int ReadPointCount(IXLCell cell, string code)
    {
        double value = ReadDouble(cell);
        if (value <= 0)
            return 0;

        if (code == "AC")
            return 1;

        return Math.Max(1, (int)Math.Round(value));
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

    double ReadDouble(IXLCell cell)
    {
        try
        {
            return cell.GetDouble();
        }
        catch
        {
            string s = cell.GetString();
            string clean = new string(s.Where(ch => char.IsDigit(ch) || ch == '.').ToArray());

            if (double.TryParse(clean, out double d))
                return d;

            return 0;
        }
    }

    string FormatFlatType(string flatType)
    {
        string value = string.IsNullOrWhiteSpace(flatType) ? "FLAT TYPE" : flatType.ToUpperInvariant();
        int dash = value.IndexOf('-');

        if (dash >= 0)
            value = value.Substring(0, dash).Trim();

        value = value.Replace("(", "").Replace(")", "").Trim();

        if (!value.Contains("FLAT"))
            value += " FLAT";

        return value;
    }

    string FormatDbType(string flatType)
    {
        string value = string.IsNullOrWhiteSpace(flatType) ? "" : flatType.ToUpperInvariant();
        int wayIndex = value.IndexOf("WAY", StringComparison.OrdinalIgnoreCase);

        if (wayIndex < 0)
            return "TPN DB";

        int start = wayIndex;
        while (start > 0 && char.IsDigit(value[start - 1]))
            start--;

        string way = value.Substring(start, wayIndex - start).Trim();
        return string.IsNullOrWhiteSpace(way) ? "TPN DB" : way + " WAY TPN DB";
    }

    string FormatPhaseLoad(Dictionary<string, double> phaseLoads, string phase)
    {
        if (phaseLoads == null || !phaseLoads.TryGetValue(phase, out double watts) || watts <= 0)
            return "0KW";

        return (watts / 1000.0).ToString("0.##") + "KW";
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
        if (h.Contains("SWITCH")) return "SW";

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

    short GetColorIndexForLayer(string layer)
    {
        if (layer == "SLD_RED") return 1;
        if (layer == "SLD_YELLOW") return 2;
        if (layer == "SLD_BLUE") return 5;
        if (layer == "SLD_GREEN") return 3;
        if (layer == "SLD_MAGENTA") return 6;
        if (layer == "SLD_TEXT") return 7;
        if (layer == "SLD_FRAME") return 8;
        return 256;
    }

    void Line(BlockTableRecord ms, Transaction tr,
        double x1, double y1, double x2, double y2, string layer)
    {
        AcLine l = new AcLine(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0));
        l.Layer = layer;
        l.Color = AcColor.FromColorIndex(AcColorMethod.ByAci, GetColorIndexForLayer(layer));

        ms.AppendEntity(l);
        tr.AddNewlyCreatedDBObject(l, true);
    }

    void Circle(BlockTableRecord ms, Transaction tr, double x, double y, double r, string layer)
    {
        Circle c = new Circle(new Point3d(x, y, 0), Vector3d.ZAxis, r);
        c.Layer = layer;
        c.Color = AcColor.FromColorIndex(AcColorMethod.ByAci, GetColorIndexForLayer(layer));

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
        t.Color = AcColor.FromColorIndex(AcColorMethod.ByAci, GetColorIndexForLayer(layer));

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