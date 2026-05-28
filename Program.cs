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
        AppDomain.CurrentDomain.AssemblyResolve += (s, a) =>
        {
            try
            {
                string folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

                if (string.IsNullOrWhiteSpace(folder))
                    return null;

                AssemblyName requested = new AssemblyName(a.Name);

                string dll = Path.Combine(folder, requested.Name + ".dll");
                if (File.Exists(dll))
                    return Assembly.LoadFrom(dll);

                dll = Path.Combine(folder, a.Name.Split(',')[0] + ".dll");
                if (File.Exists(dll))
                    return Assembly.LoadFrom(dll);

                string binFolder = Path.Combine(folder, "bin");
                if (Directory.Exists(binFolder))
                {
                    dll = Path.Combine(binFolder, requested.Name + ".dll");
                    if (File.Exists(dll))
                        return Assembly.LoadFrom(dll);
                }

                return null;
            }
            catch
            {
                return null;
            }
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

    [CommandMethod("RunAllTasks")]
    public void RunAllTasks()
    {
        Document doc = AcApp.DocumentManager.MdiActiveDocument;

        if (doc == null)
            return;

        // Step 1: Analyze connected wires
        WireAnalysisResult wireAnalysis = AnalyzeConnectedWires();

        if (wireAnalysis == null)
            return;

        // Step 2: Final wire analysis using results from step 1
        Dictionary<string, object> analysisData = FinalWireAnalysis(wireAnalysis);

        if (analysisData == null)
            return;

        // Step 3: Create graph using results from steps 1 and 2
        CreateGraph(wireAnalysis, analysisData);
    }

    WireAnalysisResult AnalyzeConnectedWires()
    {
        Document doc = AcApp.DocumentManager.MdiActiveDocument;

        if (doc == null)
            return null;

        Editor ed = doc.Editor;
        Database db = doc.Database;

        SelectionSet wireSet = GetWireSelectionSet(ed);

        if (wireSet == null)
            return null;

        PromptEntityResult entRes =
            ed.GetEntity("\nSelect object to check connections: ");

        if (entRes.Status != PromptStatus.OK)
            return null;

        List<Point3d> connectionPoints = new List<Point3d>();

        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            Entity target =
                tr.GetObject(entRes.ObjectId, OpenMode.ForRead) as Entity;

            if (target == null)
                return null;

            foreach (SelectedObject obj in wireSet)
            {
                if (obj == null)
                    continue;

                if (obj.ObjectId == entRes.ObjectId)
                    continue;

                Entity wire =
                    tr.GetObject(obj.ObjectId, OpenMode.ForRead) as Entity;

                if (wire == null)
                    continue;

                if (!(wire is Curve))
                    continue;

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

        if (p1.Status != PromptStatus.OK)
            return null;

        PromptCornerOptions p2opt =
            new PromptCornerOptions("\nSpecify opposite corner: ", p1.Value);

        PromptPointResult p2 = ed.GetCorner(p2opt);

        if (p2.Status != PromptStatus.OK)
            return null;

        PromptSelectionResult res =
            ed.SelectCrossingWindow(p1.Value, p2.Value);

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
            wire.IntersectWith(
                target,
                Intersect.OnBothOperands,
                pts,
                IntPtr.Zero,
                IntPtr.Zero);
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
    Dictionary<string, object> FinalWireAnalysis(WireAnalysisResult wireAnalysis)
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Editor ed = doc.Editor;
        Database db = doc.Database;

        PromptEntityResult per = ed.GetEntity("\nSelect a wire: ");
        if (per.Status != PromptStatus.OK)
            return null;

        PromptEntityResult p2 = ed.GetEntity("\nSelect second wire: ");
        if (p2.Status != PromptStatus.OK)
            return null;

        PromptEntityResult p3 = ed.GetEntity("\nSelect entity (Fan/Light): ");
        if (p3.Status != PromptStatus.OK)
            return null;

        Dictionary<string, object> result = new Dictionary<string, object>();

        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            Curve wire1 =
                tr.GetObject(per.ObjectId, OpenMode.ForRead) as Curve;

            Curve wire2 =
                tr.GetObject(p2.ObjectId, OpenMode.ForRead) as Curve;

            Entity ent =
                tr.GetObject(p3.ObjectId, OpenMode.ForRead) as Entity;

            if (wire1 == null || wire2 == null)
            {
                ed.WriteMessage("\nInvalid wire.");
                return null;
            }

            Point3d startPt = wire1.StartPoint;
            Point3d endPt = wire1.EndPoint;

            List<ObjectId> connected = new List<ObjectId>();

            BlockTable bt =
                (BlockTable)tr.GetObject(
                    db.BlockTableId,
                    OpenMode.ForRead);

            BlockTableRecord btr =
                (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace],
                    OpenMode.ForRead);

            foreach (ObjectId id in btr)
            {
                if (id == per.ObjectId)
                    continue;

                Entity e =
                    tr.GetObject(id, OpenMode.ForRead) as Entity;

                if (e == null)
                    continue;

                if (e is Curve)
                    continue;

                if (e is BlockReference br)
                {
                    if (IsEqual(br.Position, startPt) ||
                        IsEqual(br.Position, endPt))
                    {
                        connected.Add(id);
                    }
                }
            }

            string wireId =
                per.ObjectId.Handle.ToString().ToLower();

            List<string> ids = new List<string>();

            foreach (ObjectId id in connected)
            {
                ids.Add(id.Handle.ToString().ToLower());
            }

            bool intersect = false;
            Point3dCollection pts = new Point3dCollection();

            try
            {
                wire1.IntersectWith(
                    wire2,
                    Intersect.OnBothOperands,
                    pts,
                    IntPtr.Zero,
                    IntPtr.Zero);

                if (pts.Count > 0)
                    intersect = true;
            }
            catch
            {
                intersect = false;
            }

            Point3d center = Point3d.Origin;

            if (ent is BlockReference br2)
            {
                center = br2.Position;
            }
            else
            {
                try
                {
                    Extents3d ext = ent.GeometricExtents;

                    center = new Point3d(
                        (ext.MinPoint.X + ext.MaxPoint.X) / 2,
                        (ext.MinPoint.Y + ext.MaxPoint.Y) / 2,
                        (ext.MinPoint.Z + ext.MaxPoint.Z) / 2);
                }
                catch
                {
                }
            }

            bool yPass = false;

            if (intersect)
            {
                Point3d ip = pts[0];

                if (Math.Abs(ip.X - center.X) <= 1.0)
                    yPass = true;
            }

            result["WireId"] = wireId;
            result["ConnectedIds"] = ids;
            result["Intersect"] = intersect;
            result["YPass"] = yPass;
            result["ConnectedCount"] = connected.Count;
            result["ConnectedDevices"] = connected;

            tr.Commit();
        }

        return result;
    }

    private bool IsEqual(
        Point3d p1,
        Point3d p2,
        double tol = 1.0)
    {
        return p1.DistanceTo(p2) <= tol;
    }

    void CreateGraph(WireAnalysisResult wireAnalysis, Dictionary<string, object> analysisData)
    {
        Document doc =
            AcApp.DocumentManager.MdiActiveDocument;

        if (doc == null)
            return;

        Database db = doc.Database;
        Editor ed = doc.Editor;

        // Get insertion point from user
        PromptPointOptions ppo = new PromptPointOptions("\nSpecify insertion point for layout: ");
        PromptPointResult ppr = ed.GetPoint(ppo);

        if (ppr.Status != PromptStatus.OK)
            return;

        Point3d insertionPoint = ppr.Value;

        PromptOpenFileOptions opt =
            new PromptOpenFileOptions(
                "\nSelect Excel file: ");

        opt.Filter = "Excel Files (*.xlsx)|*.xlsx";

        PromptFileNameResult res =
            ed.GetFileNameForOpen(opt);

        if (res.Status != PromptStatus.OK)
            return;

        string file = res.StringResult;

        if (!File.Exists(file))
        {
            ed.WriteMessage("\nExcel file not found.");
            return;
        }

        List<string> worksheetNames =
            GetWorksheetNames(file);

        string worksheetName =
            SelectWorksheetName(ed, worksheetNames);

        if (string.IsNullOrWhiteSpace(worksheetName))
            return;

        Dictionary<string, List<LoadItem>> loadTable =
            ReadLoadData(file, worksheetName);

        List<Circuit> data =
            ReadCircuitData(file, worksheetName);

        string flatType = "";

        using (XLWorkbook wb =
            OpenWorkbookShared(file))
        {
            IXLWorksheet ws =
                wb.Worksheets.FirstOrDefault(
                    s => string.Equals(
                        s.Name,
                        worksheetName,
                        StringComparison.OrdinalIgnoreCase))
                ?? wb.Worksheets.First();

            flatType =
                ws.Cell("A1").GetString().Trim();

            if (string.IsNullOrWhiteSpace(flatType))
                flatType = worksheetName;
        }

        if (data.Count == 0)
        {
            ed.WriteMessage(
                "\nNo circuit rows found.");
            return;
        }
        using (doc.LockDocument())
        using (Transaction tr =
            db.TransactionManager.StartTransaction())
        {
            BlockTable bt =
                (BlockTable)tr.GetObject(
                    db.BlockTableId,
                    OpenMode.ForRead);

            BlockTableRecord ms =
                (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace],
                    OpenMode.ForWrite);

            CreateLayer(db, tr, "SLD_RED", 1);
            CreateLayer(db, tr, "SLD_YELLOW", 2);
            CreateLayer(db, tr, "SLD_BLUE", 5);
            CreateLayer(db, tr, "SLD_TEXT", 7);
            CreateLayer(db, tr, "SLD_FRAME", 8);
            CreateLayer(db, tr, "SLD_GREEN", 3);
            CreateLayer(db, tr, "SLD_MAGENTA", 6);

            double scale = 1.5;
            Text(ms, tr, flatType, (210 * scale) + insertionPoint.X, (530 * scale) + insertionPoint.Y, 18 * scale, "SLD_TEXT");

            Dictionary<string, double> phaseLoads =
                ReadPhaseLoads(file, worksheetName);

            DrawMainPanel(ms, tr, data, flatType, phaseLoads, insertionPoint);
            DrawAllCircuits(ms, tr, data, loadTable, insertionPoint);

            tr.Commit();
        }

        ed.WriteMessage("\nGraph created successfully.");
    }

    void DrawMainPanel(
        BlockTableRecord ms,
        Transaction tr,
        List<Circuit> data,
        string flatType,
        Dictionary<string, double> phaseLoads,
        Point3d insertionPoint)
    {
        double ox = insertionPoint.X;
        double oy = insertionPoint.Y;
        double scale = 1.5;

        Line(ms, tr, (170 * scale) + ox, (500 * scale) + oy, (560 * scale) + ox, (500 * scale) + oy, "SLD_BLUE");
        Line(ms, tr, (560 * scale) + ox, (500 * scale) + oy, (560 * scale) + ox, (430 * scale) + oy, "SLD_BLUE");
        Line(ms, tr, (560 * scale) + ox, (430 * scale) + oy, (170 * scale) + ox, (430 * scale) + oy, "SLD_BLUE");

        Text(ms, tr, "SAMADHAN GORAI", (175 * scale) + ox, (490 * scale) + oy, 3.2 * scale, "SLD_TEXT");
        Text(ms, tr, FormatFlatType(flatType), (175 * scale) + ox, (482 * scale) + oy, 3.2 * scale, "SLD_TEXT");
        Text(ms, tr, FormatDbType(flatType), (175 * scale) + ox, (474 * scale) + oy, 3.2 * scale, "SLD_TEXT");

        Text(ms, tr, "MAIN DB", (340 * scale) + ox, (488 * scale) + oy, 4.2 * scale, "SLD_TEXT");
        Text(ms, tr, "25A 4P MCB", (335 * scale) + ox, (475 * scale) + oy, 3.2 * scale, "SLD_TEXT");
        Text(ms, tr, "25A 4P 30mA RCCB", (328 * scale) + ox, (465 * scale) + oy, 3.2 * scale, "SLD_TEXT");

        Text(ms, tr, "R PHASE : " + FormatPhaseLoad(phaseLoads, "R"), (510 * scale) + ox, (490 * scale) + oy, 3.0 * scale, "SLD_TEXT");
        Text(ms, tr, "Y PHASE : " + FormatPhaseLoad(phaseLoads, "Y"), (510 * scale) + ox, (482 * scale) + oy, 3.0 * scale, "SLD_TEXT");
        Text(ms, tr, "B PHASE : " + FormatPhaseLoad(phaseLoads, "B"), (510 * scale) + ox, (474 * scale) + oy, 3.0 * scale, "SLD_TEXT");

        double[] xs =
        {
            230, 242, 254, 266,
            290, 302, 314, 326,
            370, 382, 394, 406
        };

        for (int i = 0; i < xs.Length && i < data.Count; i++)
        {
            Circuit c = data[i];
            Text(ms, tr, c.No, (xs[i] - 3) * scale + ox, (445 * scale) + oy, 3.2 * scale, GetLayer(c.Color));
        }
    }

    void DrawAllCircuits(
        BlockTableRecord ms,
        Transaction tr,
        List<Circuit> data,
        Dictionary<string, List<LoadItem>> loadTable,
        Point3d insertionPoint)
    {
        double ox = insertionPoint.X;
        double oy = insertionPoint.Y;
        double scale = 1.5;
        double topY = (430 * scale) + oy;

        Dictionary<string, double> xMap =
            new Dictionary<string, double>
        {
            {"R1",(230 * scale) + ox}, {"R2",(242 * scale) + ox}, {"R3",(254 * scale) + ox}, {"R4",(266 * scale) + ox},
            {"Y1",(290 * scale) + ox}, {"Y2",(302 * scale) + ox}, {"Y3",(314 * scale) + ox}, {"Y4",(326 * scale) + ox},
            {"B1",(370 * scale) + ox}, {"B2",(382 * scale) + ox}, {"B3",(394 * scale) + ox}, {"B4",(406 * scale) + ox}
        };

        double leftY = (390 * scale) + oy;
        double rightY = (390 * scale) + oy;
        double smallGap = 10 * scale;

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

            List<LoadItem> circuitLoads = null;

            if (loadTable.ContainsKey(c.No))
                circuitLoads = loadTable[c.No];

            int totalBranches = 1;

            if (circuitLoads != null && circuitLoads.Count > 0)
                totalBranches = circuitLoads.Sum(GetBranchCount);

            double lastEntityY =
                y -
                ((totalBranches - 1) * 7.0 * scale) -
                ((circuitLoads == null ? 1 : circuitLoads.Count) * 12.0 * scale);

            Line(ms, tr, x, topY, x, lastEntityY, layer);
            Circle(ms, tr, x, y, 1.6 * scale, layer);

            double textY = y - ((totalBranches * 3.5 * scale) / 2.0);

            if (circuitLoads != null && circuitLoads.Count > 0)
                DrawEntities(ms, tr, circuitLoads, y, x, layer, isLeftSide, scale);

            if (isLeftSide)
                Text(ms, tr, c.Room, x - (170 * scale), textY - 2, 3.5 * scale, "SLD_TEXT");
            else
                Text(ms, tr, c.Room, x + (120 * scale), textY - 2, 3.5 * scale, "SLD_TEXT");

            double used =
                (totalBranches * 7.0 * scale) +
                ((circuitLoads == null ? 1 : circuitLoads.Count) * 12.0 * scale) +
                smallGap +
                (10.0 * scale);

            if (c.No == "R4")
                used += 40 * scale;

            if (c.No == "Y4")
                used += 40 * scale;

            if (isLeftSide)
                leftY -= used;
            else
                rightY -= used;
        }
    }
    void DrawEntities(
    BlockTableRecord ms,
    Transaction tr,
    List<LoadItem> loads,
    double baseY,
    double busX,
    string layer,
    bool leftSide,
    double scale = 1.0)
    {
        if (loads == null || loads.Count == 0)
            return;

        double y = baseY - (8 * scale);
        double lineLength = 18 * scale;
        double branchLength = 10 * scale;
        double branchGap = 7.0 * scale;
        double entityGap = 12.0 * scale;

        foreach (LoadItem item in loads)
        {
            int branchCount = GetBranchCount(item);

            double endX = leftSide
                ? busX - lineLength
                : busX + lineLength;

            Line(ms, tr, busX, y + (1.2 * scale), endX, y + (1.2 * scale), layer);
            Circle(ms, tr, busX, y + (1.2 * scale), 1.1 * scale, layer);

            double branchX;
            double symbolX;
            double labelX;

            if (leftSide)
            {
                branchX = endX + branchLength;
                symbolX = branchX - (12 * scale);
                labelX = symbolX - (30 * scale);
            }
            else
            {
                branchX = endX - branchLength;
                symbolX = branchX + (12 * scale);
                labelX = symbolX + (6 * scale);
            }

            double firstY = y + (1.2 * scale);
            double lastY =
                y - ((branchCount - 1) * branchGap) + (1.2 * scale);

            Line(ms, tr, branchX, firstY, branchX, lastY, layer);

            for (int b = 0; b < branchCount; b++)
            {
                double by = y - (b * branchGap) + (1.2 * scale);
                double sX = (endX + branchX) / 2.0;

                Line(ms, tr, endX, by, branchX, by, layer);

                DrawSwitchSymbol(ms, tr, sX, by, leftSide, item.Code, scale);

                if (item.Code != "SW")
                    DrawEntitySymbol(
                        ms,
                        tr,
                        item.Code,
                        symbolX,
                        by,
                        leftSide,
                        layer,
                        scale);

                string name = item.Code;

                if (name == "TL")
                    name = "Tube Light";

                if (name == "SW")
                    name = "Switch";

                if (name == "CF")
                    name = "F";

                bool joinNoSpace =
                    name == "BL" ||
                    name == "CL" ||
                    name == "TL" ||
                    name == "F";

                string label;

                if (branchCount == 1)
                {
                    label = joinNoSpace ? name + "1" : name;
                }
                else
                {
                    label = joinNoSpace
                        ? name + (b + 1)
                        : name + " " + (b + 1);
                }

                Text(ms, tr, label, labelX, by - (1.3 * scale), 2.5 * scale, "SLD_GREEN");
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

        if (count < 1)
            count = 1;

        if (count > 15)
            count = 15;

        return count;
    }

    void DrawSwitchSymbol(
        BlockTableRecord ms,
        Transaction tr,
        double x,
        double y,
        bool leftSide,
        string code,
        double scale = 1.0)
    {
        string switchLayer = "SLD_MAGENTA";
        int switchCount = GetSwitchCount(code);

        if (switchCount == 2)
        {
            DrawTwoWaySwitchSymbol(
                ms,
                tr,
                x,
                y,
                leftSide,
                switchLayer,
                scale);

            return;
        }

        double spacing = 2.1 * scale;

        for (int i = 0; i < switchCount; i++)
        {
            double dx =
                (i - (switchCount - 1) / 2.0) * spacing;

            if (leftSide)
            {
                Circle(ms, tr, x + (0.75 * scale) + dx, y, 0.22 * scale, switchLayer);
                Line(ms, tr, x + (0.75 * scale) + dx, y,
                    x - (0.75 * scale) + dx, y + (1.95 * scale), switchLayer);
            }
            else
            {
                Circle(ms, tr, x - (0.75 * scale) - dx, y, 0.22 * scale, switchLayer);
                Line(ms, tr, x - (0.75 * scale) - dx, y,
                    x + (0.75 * scale) - dx, y + (1.95 * scale), switchLayer);
            }
        }
    }

    void DrawTwoWaySwitchSymbol(
        BlockTableRecord ms,
        Transaction tr,
        double x,
        double y,
        bool leftSide,
        string switchLayer,
        double scale = 1.0)
    {
        double armHalfWidth = 1.5 * scale;
        double armHeight = 2.0 * scale;
        double circleRadius = 0.35 * scale;

        double apexX = x;
        double apexY = y;
        double leftX = x - armHalfWidth;
        double leftY = y + armHeight;
        double rightX = x + armHalfWidth;
        double rightY = y + armHeight;
        double circleCenterY = y - armHeight * 0.5;

        Line(ms, tr, apexX, apexY, leftX, leftY, switchLayer);
        Line(ms, tr, apexX, apexY, rightX, rightY, switchLayer);
        Line(ms, tr, apexX, apexY,
            apexX, circleCenterY + circleRadius, switchLayer);

        Circle(ms, tr, apexX, circleCenterY, circleRadius, switchLayer);
    }

    int GetSwitchCount(string code)
    {
        if (code == "SO" ||
            code == "CHG" ||
            code == "CTV" ||
            code == "SS")
        {
            return 2;
        }

        return 1;
    }
    void DrawEntitySymbol(
    BlockTableRecord ms,
    Transaction tr,
    string code,
    double x,
    double y,
    bool leftSide,
    string phaseLayer,
    double scale = 1.0)
    {
        string accentLayer = "SLD_MAGENTA";
        string detailLayer = "SLD_GREEN";
        string symbolLayer = GetSymbolLayer(code, phaseLayer);

        if (code == "BL")
        {
            Circle(ms, tr, x, y, 1.6 * scale, symbolLayer);
            Line(ms, tr, x - (3.2 * scale), y, x - (1.6 * scale), y, symbolLayer);
            Line(ms, tr, x + (1.6 * scale), y, x + (3.2 * scale), y, symbolLayer);
            Circle(ms, tr, x, y - (2.4 * scale), 0.55 * scale, accentLayer);
        }
        else if (code == "CL")
        {
            Circle(ms, tr, x, y, 2.4 * scale, symbolLayer);
            Circle(ms, tr, x, y, 1.4 * scale, symbolLayer);
            Line(ms, tr, x - (2.4 * scale), y, x + (2.4 * scale), y, symbolLayer);
            Line(ms, tr, x, y - (2.4 * scale), x, y + (2.4 * scale), symbolLayer);
        }
        else if (code == "TL")
        {
            Rect(ms, tr, x - (3.2 * scale), y - (0.9 * scale), x + (3.2 * scale), y + (0.9 * scale), symbolLayer);
            Line(ms, tr, x - (2.6 * scale), y, x + (2.6 * scale), y, symbolLayer);
        }
        else if (code == "CF")
        {
            Circle(ms, tr, x, y, 1.2 * scale, symbolLayer);
            Line(ms, tr, x, y, x + (4 * scale), y + (2 * scale), symbolLayer);
            Line(ms, tr, x, y, x - (4 * scale), y + (2 * scale), symbolLayer);
            Line(ms, tr, x, y, x, y - (4 * scale), symbolLayer);
        }
        else if (code == "SO" ||
                 code == "CHG" ||
                 code == "CTV" ||
                 code == "SS")
        {
            Rect(ms, tr, x - (2.2 * scale), y - (1.6 * scale), x + (2.2 * scale), y + (1.6 * scale), symbolLayer);
            Circle(ms, tr, x - (0.8 * scale), y, 0.3 * scale, accentLayer);
            Circle(ms, tr, x + (0.8 * scale), y, 0.3 * scale, detailLayer);
        }
        else if (code == "AC")
        {
            Rect(ms, tr, x - (4 * scale), y - (1.6 * scale), x + (4 * scale), y + (1.6 * scale), symbolLayer);
            Line(ms, tr, x - (2.5 * scale), y - (2.5 * scale), x + (2.5 * scale), y - (2.5 * scale), symbolLayer);
        }
        else if (code == "GY")
        {
            Circle(ms, tr, x, y, 2.3 * scale, symbolLayer);
            Line(ms, tr, x - (1.5 * scale), y + (2.3 * scale), x + (1.5 * scale), y + (2.3 * scale), symbolLayer);
        }
        else if (code == "SW")
        {
            DrawSwitchSymbol(ms, tr, x, y, leftSide, code, scale);
        }
        else if (code == "REF" ||
                 code == "WM" ||
                 code == "WP" ||
                 code == "MX" ||
                 code == "MW")
        {
            Rect(ms, tr, x - (2.5 * scale), y - (3 * scale), x + (2.5 * scale), y + (3 * scale), symbolLayer);
            Line(ms, tr, x - (1.5 * scale), y, x + (1.5 * scale), y, symbolLayer);
        }
        else
        {
            Circle(ms, tr, x, y, 1.8 * scale, symbolLayer);
        }
    }

    string GetSymbolLayer(string code, string phaseLayer)
    {
        if (code == "BL" ||
            code == "CF" ||
            code == "EF" ||
            code == "FL")
        {
            return "SLD_BLUE";
        }

        if (code == "TL" ||
            code == "CL" ||
            code == "BB")
        {
            return "SLD_RED";
        }

        if (code == "SW")
            return "SLD_MAGENTA";

        return phaseLayer;
    }

    List<Circuit> ReadCircuitData(
        string path,
        string worksheetName)
    {
        List<Circuit> circuits = new List<Circuit>();

        using (XLWorkbook wb = OpenWorkbookShared(path))
        {
            IXLWorksheet ws =
                wb.Worksheets.FirstOrDefault(
                    x => string.Equals(
                        x.Name,
                        worksheetName,
                        StringComparison.OrdinalIgnoreCase))
                ?? wb.Worksheets.First();

            for (int r = 10; r <= 30; r++)
            {
                string refNo =
                    GetAccurateCircuitReference(ws, r);

                string room =
                    ws.Cell(r, 8).GetString().Trim();

                if (string.IsNullOrWhiteSpace(refNo))
                    continue;

                if (!(refNo.StartsWith("R", StringComparison.OrdinalIgnoreCase) ||
                      refNo.StartsWith("Y", StringComparison.OrdinalIgnoreCase) ||
                      refNo.StartsWith("B", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                circuits.Add(new Circuit
                {
                    No = refNo,
                    Room = string.IsNullOrWhiteSpace(room) ? refNo : room,
                    Color =
                        refNo.StartsWith("R", StringComparison.OrdinalIgnoreCase)
                            ? (short)1
                            : refNo.StartsWith("Y", StringComparison.OrdinalIgnoreCase)
                                ? (short)2
                                : (short)5
                });
            }
        }

        return circuits;
    }

    Dictionary<string, List<LoadItem>> ReadLoadData(
        string path,
        string worksheetName)
    {
        Dictionary<string, List<LoadItem>> dict =
            new Dictionary<string, List<LoadItem>>();

        using (XLWorkbook wb = OpenWorkbookShared(path))
        {
            IXLWorksheet ws =
                wb.Worksheets.FirstOrDefault(
                    x => string.Equals(
                        x.Name,
                        worksheetName,
                        StringComparison.OrdinalIgnoreCase))
                ?? wb.Worksheets.First();

            int headerRow = 8;
            int wattRow = 9;

            for (int r = 10; r <= 30; r++)
            {
                string refNo =
                    GetAccurateCircuitReference(ws, r);

                if (string.IsNullOrWhiteSpace(refNo))
                    continue;

                if (!(refNo.StartsWith("R") ||
                      refNo.StartsWith("Y") ||
                      refNo.StartsWith("B")))
                {
                    continue;
                }

                List<LoadItem> list = new List<LoadItem>();

                for (int c = 9; c <= 28; c++)
                {
                    string header =
                        ws.Cell(headerRow, c).GetString().Trim();

                    if (string.IsNullOrWhiteSpace(header))
                        header = ws.Cell(7, c).GetString().Trim();

                    string code = MapHeaderToCode(header);
                    int watt = ReadInt(ws.Cell(wattRow, c));
                    int count = ReadPointCount(ws.Cell(r, c), code);

                    if (count > 0 &&
                        watt > 0 &&
                        code != "")
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
    Dictionary<string, double> ReadPhaseLoads(
    string path,
    string worksheetName)
    {
        Dictionary<string, double> phaseLoads =
            new Dictionary<string, double>
            {
                { "R", 0 },
                { "Y", 0 },
                { "B", 0 }
            };

        using (XLWorkbook wb = OpenWorkbookShared(path))
        {
            IXLWorksheet ws =
                wb.Worksheets.FirstOrDefault(
                    x => string.Equals(
                        x.Name,
                        worksheetName,
                        StringComparison.OrdinalIgnoreCase))
                ?? wb.Worksheets.First();

            for (int r = 10; r <= 30; r++)
            {
                string phase = GetCircuitPhase(ws, r);

                if (string.IsNullOrWhiteSpace(phase))
                    continue;

                double load =
                    ReadDouble(ws.Cell(r, PhaseLoadColumn(phase)));

                phaseLoads[phase] += load;
            }
        }

        return phaseLoads;
    }

    string GetAccurateCircuitReference(
        IXLWorksheet ws,
        int row)
    {
        string existing =
            ws.Cell(row, 4).GetString().Trim().ToUpperInvariant();

        string phase = GetCircuitPhase(ws, row);
        int serial = ReadInt(ws.Cell(row, 3));

        if (!string.IsNullOrWhiteSpace(phase) &&
            serial >= 1 &&
            serial <= 4)
        {
            return phase + serial;
        }

        return existing;
    }

    string GetCircuitPhase(IXLWorksheet ws, int row)
    {
        string[] phases = { "R", "Y", "B" };

        foreach (string phase in phases)
        {
            if (ReadDouble(ws.Cell(row, PhaseLoadColumn(phase))) > 0)
                return phase;
        }

        string existing =
            ws.Cell(row, 4).GetString().Trim().ToUpperInvariant();

        if (existing.StartsWith("R"))
            return "R";

        if (existing.StartsWith("Y"))
            return "Y";

        if (existing.StartsWith("B"))
            return "B";

        return "";
    }

    int PhaseLoadColumn(string phase)
    {
        if (phase == "R")
            return 29;

        if (phase == "Y")
            return 30;

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

            string clean =
                new string(
                    s.Where(ch =>
                        char.IsDigit(ch) ||
                        ch == '.').ToArray());

            double d;

            if (double.TryParse(clean, out d))
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

            string clean =
                new string(
                    s.Where(ch =>
                        char.IsDigit(ch) ||
                        ch == '.').ToArray());

            double d;

            if (double.TryParse(clean, out d))
                return d;

            return 0;
        }
    }

    string FormatFlatType(string flatType)
    {
        string value =
            string.IsNullOrWhiteSpace(flatType)
                ? "FLAT TYPE"
                : flatType.ToUpperInvariant();

        int dash = value.IndexOf('-');

        if (dash >= 0)
            value = value.Substring(0, dash).Trim();

        value =
            value.Replace("(", "")
                 .Replace(")", "")
                 .Trim();

        if (!value.Contains("FLAT"))
            value += " FLAT";

        return value;
    }

    string FormatDbType(string flatType)
    {
        string value =
            string.IsNullOrWhiteSpace(flatType)
                ? ""
                : flatType.ToUpperInvariant();

        int wayIndex =
            value.IndexOf(
                "WAY",
                StringComparison.OrdinalIgnoreCase);

        if (wayIndex < 0)
            return "TPN DB";

        int start = wayIndex;

        while (start > 0 && char.IsDigit(value[start - 1]))
        {
            start--;
        }

        string way =
            value.Substring(start, wayIndex - start).Trim();

        if (string.IsNullOrWhiteSpace(way))
            return "TPN DB";

        return way + " WAY TPN DB";
    }

    string FormatPhaseLoad(
        Dictionary<string, double> phaseLoads,
        string phase)
    {
        double watts;

        if (phaseLoads == null ||
            !phaseLoads.TryGetValue(phase, out watts) ||
            watts <= 0)
        {
            return "0KW";
        }

        return (watts / 1000.0).ToString("0.##") + "KW";
    }
    string MapHeaderToCode(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return "";

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

        if (header.Length > 4)
            return header.Substring(0, 4).ToUpperInvariant();

        return header.ToUpperInvariant();
    }

    string GetLayer(short color)
    {
        if (color == 1)
            return "SLD_RED";

        if (color == 2)
            return "SLD_YELLOW";

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

    void Line(
        BlockTableRecord ms,
        Transaction tr,
        double x1,
        double y1,
        double x2,
        double y2,
        string layer)
    {
        AcLine l =
            new AcLine(
                new Point3d(x1, y1, 0),
                new Point3d(x2, y2, 0));

        l.Layer = layer;
        l.Color =
            AcColor.FromColorIndex(
                AcColorMethod.ByAci,
                GetColorIndexForLayer(layer));

        ms.AppendEntity(l);
        tr.AddNewlyCreatedDBObject(l, true);
    }

    void Circle(
        BlockTableRecord ms,
        Transaction tr,
        double x,
        double y,
        double r,
        string layer)
    {
        Circle c =
            new Circle(
                new Point3d(x, y, 0),
                Vector3d.ZAxis,
                r);

        c.Layer = layer;
        c.Color =
            AcColor.FromColorIndex(
                AcColorMethod.ByAci,
                GetColorIndexForLayer(layer));

        ms.AppendEntity(c);
        tr.AddNewlyCreatedDBObject(c, true);
    }

    void Rect(
        BlockTableRecord ms,
        Transaction tr,
        double x1,
        double y1,
        double x2,
        double y2,
        string layer)
    {
        Line(ms, tr, x1, y1, x2, y1, layer);
        Line(ms, tr, x2, y1, x2, y2, layer);
        Line(ms, tr, x2, y2, x1, y2, layer);
        Line(ms, tr, x1, y2, x1, y1, layer);
    }

    void Text(
        BlockTableRecord ms,
        Transaction tr,
        string value,
        double x,
        double y,
        double h,
        string layer)
    {
        DBText t = new DBText();

        t.Position = new Point3d(x, y, 0);
        t.TextString = value ?? "";
        t.Height = h;
        t.Layer = layer;
        t.Color =
            AcColor.FromColorIndex(
                AcColorMethod.ByAci,
                GetColorIndexForLayer(layer));

        ms.AppendEntity(t);
        tr.AddNewlyCreatedDBObject(t, true);
    }
    List<string> GetWorksheetNames(string path)
    {
        List<string> names = new List<string>();

        using (XLWorkbook wb = OpenWorkbookShared(path))
        {
            foreach (IXLWorksheet ws in wb.Worksheets)
            {
                names.Add(ws.Name);
            }
        }

        return names;
    }

    string SelectWorksheetName(
        Editor ed,
        List<string> worksheetNames)
    {
        if (worksheetNames == null ||
            worksheetNames.Count == 0)
        {
            return "";
        }

        if (worksheetNames.Count == 1)
            return worksheetNames[0];

        PromptKeywordOptions opt =
            new PromptKeywordOptions("\nSelect worksheet: ");

        foreach (string name in worksheetNames)
        {
            string safeName =
                name.Replace(" ", "_")
                    .Replace("-", "_");

            opt.Keywords.Add(safeName);
        }

        opt.AllowNone = false;

        PromptResult res = ed.GetKeywords(opt);

        if (res.Status != PromptStatus.OK)
            return "";

        foreach (string name in worksheetNames)
        {
            string safeName =
                name.Replace(" ", "_")
                    .Replace("-", "_");

            if (string.Equals(
                    safeName,
                    res.StringResult,
                    StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return worksheetNames[0];
    }

    XLWorkbook OpenWorkbookShared(string path)
    {
        FileStream fs =
            new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);

        return new XLWorkbook(fs);
    }

    void CreateLayer(
        Database db,
        Transaction tr,
        string name,
        short color)
    {
        LayerTable lt =
            (LayerTable)tr.GetObject(
                db.LayerTableId,
                OpenMode.ForRead);

        if (!lt.Has(name))
        {
            lt.UpgradeOpen();

            LayerTableRecord ltr =
                new LayerTableRecord();

            ltr.Name = name;
            ltr.Color =
                AcColor.FromColorIndex(
                    AcColorMethod.ByAci,
                    color);

            lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
        }
    }
}
