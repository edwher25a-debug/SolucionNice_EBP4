// =====================================================================================
//  TrocearPlanos - lado de AutoCAD / Civil 3D del proceso "Planos desde CAD"
// =====================================================================================
//
//  Produce, por cada dwg de plano:
//      <codigo>.json          el manifiesto (contrato con el lado de Revit)
//      <codigo>-Vnn.dwg       un dwg por viewport, con SOLO geometria, recentrado
//
//  Los textos NO van en el dwg: viajan en el manifiesto, porque la API de Revit no
//  expone el texto de un CAD importado. Las cotas y las directrices se revientan: sus
//  lineas van al dwg y su texto al manifiesto.
//
//  No modifica el dibujo de origen. Para poder clonar las entidades reventadas hay que
//  darles un ObjectId, asi que se anexan al model space, se clonan al dwg de salida y
//  se borran antes de cerrar la transaccion. El neto sobre el dibujo es cero.
//
//  -------------------------------------------------------------------------------------
//  Para probarlo desde el MCP de Civil 3D (run_code), el cuerpo es:
//
//      return TrocearPlanos.Ejecutar(doc, db, tr, @"C:\Trabajo\PlanosCAD", false);
//
//  (el ultimo parametro es "soloAnalizar": true no escribe ningun archivo)
//  Para un add-in de AutoCAD, llamar a Ejecutar desde un IExtensionApplication/CommandMethod.
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;
using AcCurve = Autodesk.AutoCAD.DatabaseServices.Curve;

public static class TrocearPlanos
{
    //Capas que nunca se exportan: el rotulo lo pone Revit y Defpoints no se imprime
    private static readonly string[] CapasExcluidas = { "01CAIXET", "DEFPOINTS" };

    //El nombre tiene que cumplir el patron. Si no, se para: un codigo mal deducido en
    //un lote de treinta planos es peor que un plano sin procesar.
    private static readonly Regex PatronNombre = new Regex(@"^(PL-[A-Za-z]{2}-[A-Za-z0-9]+-\d{3})-(.+)$", RegexOptions.Compiled);

    private const int ProfundidadMaxima = 4;   //bloques dentro de bloques dentro de cotas

    // =================================================================================
    //  Entrada
    // =================================================================================

    public static object Ejecutar(Document doc, Database db, Transaction tr, string carpetaSalida, bool soloAnalizar)
    {
        string archivo = Path.GetFileName(db.Filename ?? "");
        string sinExtension = Path.GetFileNameWithoutExtension(archivo);

        Match m = PatronNombre.Match(sinExtension);
        if (!m.Success)
            return new { error = $"el nombre \"{sinExtension}\" no cumple el patron PL-XX-YYY-nnn-descripcion" };

        string codigo = m.Groups[1].Value;
        string titulo = m.Groups[2].Value.Trim();

        double modeloMm = MmPorUnidad(db.Insunits);
        if (modeloMm <= 0)
            return new { error = $"el dibujo no tiene unidades utilizables (INSUNITS = {db.Insunits})" };

        var avisos = new List<string>();
        var vistas = new List<Vista>();
        double papelAnchoMm = 0, papelAltoMm = 0;
        string layoutUsado = "";

        var layDict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        var layouts = new List<Layout>();
        foreach (DBDictionaryEntry de in layDict)
        {
            var lay = (Layout)tr.GetObject(de.Value, OpenMode.ForRead);
            if (lay.LayoutName == "Model") continue;
            layouts.Add(lay);
        }

        if (layouts.Count == 0) return new { error = "el dibujo no tiene ningun layout de impresion" };
        if (layouts.Count > 1)
            avisos.Add($"el dibujo tiene {layouts.Count} layouts; se tomo \"{layouts[0].LayoutName}\". "
                       + "Con varios layouts el codigo del nombre ya no identifica la hoja: hay que combinarlo con el nombre del layout.");

        Layout layout = layouts[0];
        layoutUsado = layout.LayoutName;
        TamanoPapel(layout, out papelAnchoMm, out papelAltoMm);

        //=== 01_Clasificar los viewports ===
        var candidatos = new List<Candidato>();
        foreach (ObjectId vpId in layout.GetViewports())
        {
            var vp = tr.GetObject(vpId, OpenMode.ForRead) as Viewport;
            if (vp == null || vp.Number == 1) continue;

            string motivo = Clasificar(vp);
            if (motivo != "")
            {
                avisos.Add($"viewport {vp.Number} (handle {vp.Handle}): {motivo}");
                continue;
            }

            candidatos.Add(Medir(vp, modeloMm, PapelMm(layout)));
        }

        if (candidatos.Count == 0) return new { error = "no hay ningun viewport procesable en el layout", avisos };

        //=== 02_Nombrar en orden de lectura: Y descendente y luego X ascendente ===
        candidatos.Sort((a, b) =>
        {
            int porY = b.PapelCentroY.CompareTo(a.PapelCentroY);
            if (porY != 0) return porY;
            int porX = a.PapelCentroX.CompareTo(b.PapelCentroX);
            return porX != 0 ? porX : string.CompareOrdinal(a.Handle, b.Handle);
        });
        for (int i = 0; i < candidatos.Count; i++)
            candidatos[i].Nombre = $"{codigo}-V{(i + 1):00}";

        //=== 03_Procesar cada viewport ===
        if (!soloAnalizar) Directory.CreateDirectory(carpetaSalida);

        var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);

        foreach (Candidato c in candidatos)
        {
            var vista = new Vista
            {
                Nombre = c.Nombre,
                Handle = c.Handle,
                Dwg = c.Nombre + ".dwg",
                EscalaDenominador = (int)Math.Round(modeloMm / c.CustomScaleMm),
                CustomScale = c.CustomScale,
                VentanaAnchoM = c.AnchoM,
                VentanaAltoM = c.AltoM,
                VentanaCentroX = c.CentroX,
                VentanaCentroY = c.CentroY,
                PapelCentroXMm = c.PapelCentroX,
                PapelCentroYMm = c.PapelCentroY
            };

            var idsGeometria = new List<ObjectId>();
            var temporales = new List<ObjectId>();

            try
            {
                Recolectar(db, tr, ms, c, modeloMm, idsGeometria, temporales, vista.Textos, avisos);

                if (!soloAnalizar && idsGeometria.Count > 0)
                    Escribir(db, Path.Combine(carpetaSalida, vista.Dwg), idsGeometria, c);

                vista.Entidades = idsGeometria.Count;
            }
            finally
            {
                //Lo que se anexo para poder clonarlo se borra: el dibujo queda como estaba
                foreach (ObjectId id in temporales)
                {
                    try
                    {
                        var ent = tr.GetObject(id, OpenMode.ForWrite, false, true) as AcEntity;
                        if (ent != null && !ent.IsErased) ent.Erase();
                    }
                    catch { }
                }
            }

            vistas.Add(vista);
        }

        //=== 04_Manifiesto ===
        string json = Manifiesto(codigo, titulo, archivo, layoutUsado, papelAnchoMm, papelAltoMm, vistas, avisos);
        string rutaManifiesto = Path.Combine(carpetaSalida, codigo + ".json");
        if (!soloAnalizar) File.WriteAllText(rutaManifiesto, json, new UTF8Encoding(false));

        var resumen = new List<object>();
        foreach (Vista v in vistas)
            resumen.Add(new { v.Nombre, escala = "1:" + v.EscalaDenominador, entidades = v.Entidades, textos = v.Textos.Count, ventana = new[] { Math.Round(v.VentanaAnchoM, 2), Math.Round(v.VentanaAltoM, 2) } });

        return new
        {
            codigo,
            titulo,
            layout = layoutUsado,
            papelMm = new[] { Math.Round(papelAnchoMm, 1), Math.Round(papelAltoMm, 1) },
            soloAnalizar,
            manifiesto = soloAnalizar ? "(no escrito)" : rutaManifiesto,
            vistas = resumen,
            avisos
        };
    }

    // =================================================================================
    //  Clasificacion y medida de viewports
    // =================================================================================

    private static string Clasificar(Viewport vp)
    {
        if (!vp.On) return "apagado, se salta";

        double cs;
        try { cs = vp.CustomScale; } catch { return "no se pudo leer la escala, se salta"; }
        if (cs <= 1e-12) return "escala cero o invalida, se salta";

        Vector3d dir = vp.ViewDirection;
        if (dir.Length < 1e-12 || Math.Abs(dir.GetNormal().Z) < 0.999)
            return "es una vista 3D o isometrica: no se puede aplanar a una vista de dibujo, hay que hacerla a mano";

        if (vp.NonRectClipOn && vp.NonRectClipEntityId != ObjectId.Null)
            return "recortado con una polilinea: se tomo el rectangulo envolvente, revisar el resultado";

        return "";
    }

    private static Candidato Medir(Viewport vp, double modeloMm, double papelFactorMm)
    {
        double cs = vp.CustomScale;

        //Tamano de la ventana en unidades de modelo, y de ahi a metros
        double anchoModelo = vp.Width / cs;
        double altoModelo = vp.Height / cs;

        double giro = vp.TwistAngle;
        double cosG = Math.Abs(Math.Cos(giro)), senG = Math.Abs(Math.Sin(giro));

        //Capas congeladas SOLO en este viewport: es lo que un Wblock por ventana se salta
        var congeladas = new List<ObjectId>();
        try
        {
            ObjectIdCollection lista = vp.GetFrozenLayers();
            if (lista != null) foreach (ObjectId id in lista) congeladas.Add(id);
        }
        catch { }

        return new Candidato
        {
            Handle = vp.Handle.ToString(),
            CapasCongeladas = congeladas,
            CustomScale = cs,
            CustomScaleMm = cs * papelFactorMm,
            Giro = giro,
            AnchoM = anchoModelo * modeloMm / 1000.0,
            AltoM = altoModelo * modeloMm / 1000.0,
            CentroX = vp.ViewTarget.X + vp.ViewCenter.X,
            CentroY = vp.ViewTarget.Y + vp.ViewCenter.Y,
            //Caja alineada con los ejes que envuelve a la ventana, girada o no. Es un superconjunto
            //a proposito: el recorte de la vista de Revit hace el recorte exacto.
            SeleccionAncho = anchoModelo * cosG + altoModelo * senG,
            SeleccionAlto = anchoModelo * senG + altoModelo * cosG,
            PapelCentroX = vp.CenterPoint.X * papelFactorMm,
            PapelCentroY = vp.CenterPoint.Y * papelFactorMm
        };
    }

    // =================================================================================
    //  Recoleccion de entidades
    // =================================================================================

    private static void Recolectar(Database db, Transaction tr, BlockTableRecord ms, Candidato c, double modeloMm,
        List<ObjectId> idsGeometria, List<ObjectId> temporales, List<Texto> textos, List<string> avisos)
    {
        var congeladas = new HashSet<ObjectId>();
        foreach (ObjectId id in c.CapasCongeladas) congeladas.Add(id);

        double x0 = c.CentroX - c.SeleccionAncho / 2, x1 = c.CentroX + c.SeleccionAncho / 2;
        double y0 = c.CentroY - c.SeleccionAlto / 2, y1 = c.CentroY + c.SeleccionAlto / 2;

        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        var visible = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectId id in lt)
        {
            var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
            bool ok = !l.IsOff && !l.IsFrozen && !congeladas.Contains(id);
            foreach (string excluida in CapasExcluidas)
                if (string.Equals(l.Name, excluida, StringComparison.OrdinalIgnoreCase)) ok = false;
            visible[l.Name] = ok;
        }

        int sinExtents = 0;

        //Primera pasada: solo seleccionar. Reventar entidades anexa al model space, y no se
        //puede anexar a un BlockTableRecord mientras se esta recorriendo.
        var enVentana = new List<ObjectId>();

        foreach (ObjectId id in ms)
        {
            AcEntity ent = null;
            try { ent = tr.GetObject(id, OpenMode.ForRead) as AcEntity; } catch { }
            if (ent == null) continue;

            bool capaOk;
            if (!visible.TryGetValue(ent.Layer, out capaOk) || !capaOk) continue;

            //Criterio: sus extents TOCAN la ventana. No "su centro esta dentro": una linea que
            //cruza el borde se ve recortada en AutoCAD y tiene que llegar a Revit.
            Extents3d ex;
            try { ex = ent.GeometricExtents; }
            catch { sinExtents++; continue; }

            if (ex.MaxPoint.X < x0 || ex.MinPoint.X > x1 || ex.MaxPoint.Y < y0 || ex.MinPoint.Y > y1) continue;

            enVentana.Add(id);
        }

        //Segunda pasada: clasificar y reventar
        foreach (ObjectId id in enVentana)
        {
            AcEntity ent = null;
            try { ent = tr.GetObject(id, OpenMode.ForRead) as AcEntity; } catch { }
            if (ent == null) continue;

            Clasificar(db, tr, ms, ent, c, modeloMm, 0, idsGeometria, temporales, textos, avisos);
        }

        if (sinExtents > 0)
            avisos.Add($"{c.Nombre}: {sinExtents} entidad(es) sin extents, no se pudieron clasificar en ninguna ventana");
    }

    //Reparte una entidad entre geometria y textos, reventando lo que haga falta
    private static void Clasificar(Database db, Transaction tr, BlockTableRecord ms, AcEntity ent, Candidato c,
        double modeloMm, int profundidad, List<ObjectId> idsGeometria, List<ObjectId> temporales,
        List<Texto> textos, List<string> avisos)
    {
        //--- Texto ---
        var dbText = ent as DBText;
        if (dbText != null)
        {
            textos.Add(ATexto(dbText.TextString, dbText.Position, dbText.Height, dbText.Rotation, dbText.Layer, c, modeloMm));
            return;
        }

        var mText = ent as MText;
        if (mText != null)
        {
            textos.Add(ATexto(LimpiarMText(mText.Contents), mText.Location, mText.TextHeight, mText.Rotation, mText.Layer, c, modeloMm));
            return;
        }

        //--- Geometria directa: Line, Arc, Circle, Polyline, Spline, Ellipse... ---
        if (ent is AcCurve)
        {
            if (!ent.ObjectId.IsNull) idsGeometria.Add(ent.ObjectId);
            else Anexar(tr, ms, ent, idsGeometria, temporales);
            return;
        }

        //--- Solido 2D: casi siempre una punta de flecha. Se pasa su contorno. ---
        var solido = ent as Solid;
        if (solido != null)
        {
            AcEntity contorno = ContornoDeSolido(solido);
            if (contorno != null) Anexar(tr, ms, contorno, idsGeometria, temporales);
            return;
        }

        //--- Lo que hay que reventar: cotas, directrices, bloques, tablas, proxies ---
        bool reventable = ent is Dimension || ent is Leader || ent is MLeader || ent is BlockReference
                          || ent is Table || ent is ProxyEntity;

        if (reventable && profundidad < ProfundidadMaxima)
        {
            var pedazos = new DBObjectCollection();
            try { ent.Explode(pedazos); }
            catch (Exception ex)
            {
                avisos.Add($"{c.Nombre}: no se pudo reventar {ent.GetType().Name} (handle {ent.Handle}): {ex.Message}");
                pedazos.Dispose();
                return;
            }

            foreach (DBObject pedazo in pedazos)
            {
                var hijo = pedazo as AcEntity;
                if (hijo == null) { pedazo.Dispose(); continue; }

                //Un pedazo hereda la capa de su padre si estaba en capa 0
                if (string.IsNullOrEmpty(hijo.Layer) || hijo.Layer == "0") hijo.Layer = ent.Layer;

                Clasificar(db, tr, ms, hijo, c, modeloMm, profundidad + 1, idsGeometria, temporales, textos, avisos);
            }

            pedazos.Dispose();
            return;
        }

        avisos.Add($"{c.Nombre}: {ent.GetType().Name} (handle {ent.Handle}) no se convierte, revisar a mano");
    }

    //Anexa una entidad reventada al model space para darle un ObjectId clonable.
    //Se borra al final: el dibujo de origen queda intacto.
    private static void Anexar(Transaction tr, BlockTableRecord ms, AcEntity ent, List<ObjectId> idsGeometria, List<ObjectId> temporales)
    {
        ObjectId id = ms.AppendEntity(ent);
        tr.AddNewlyCreatedDBObject(ent, true);
        idsGeometria.Add(id);
        temporales.Add(id);
    }

    private static AcEntity ContornoDeSolido(Solid solido)
    {
        try
        {
            var puntos = new List<Point3d>();
            for (short i = 0; i < 4; i++)
            {
                Point3d p = solido.GetPointAt(i);
                if (puntos.Count == 0 || p.DistanceTo(puntos[puntos.Count - 1]) > 1e-9) puntos.Add(p);
            }
            if (puntos.Count < 3) return null;

            var pl = new Polyline();
            for (int i = 0; i < puntos.Count; i++) pl.AddVertexAt(i, new Point2d(puntos[i].X, puntos[i].Y), 0, 0, 0);
            pl.Closed = true;
            pl.Layer = solido.Layer;
            return pl;
        }
        catch { return null; }
    }

    private static Texto ATexto(string contenido, Point3d posicion, double alturaModelo, double rotacion, string capa,
        Candidato c, double modeloMm)
    {
        //La altura en papel es la que decide el tipo de texto de Revit. Aqui es donde las dos
        //escalas del dibujo (1:20 y 1:25) colapsan en un solo juego de alturas.
        double alturaMm = alturaModelo * c.CustomScaleMm;

        //Posicion relativa al centro de la ventana, deshaciendo el giro del viewport
        double dx = posicion.X - c.CentroX, dy = posicion.Y - c.CentroY;
        if (Math.Abs(c.Giro) > 1e-12)
        {
            double cg = Math.Cos(-c.Giro), sg = Math.Sin(-c.Giro);
            double rx = dx * cg - dy * sg, ry = dx * sg + dy * cg;
            dx = rx; dy = ry;
        }

        return new Texto
        {
            Contenido = contenido ?? "",
            XM = dx * modeloMm / 1000.0,
            YM = dy * modeloMm / 1000.0,
            AlturaMm = alturaMm,
            RotacionGrados = (rotacion - c.Giro) * 180.0 / Math.PI,
            Capa = capa ?? ""
        };
    }

    //Quita los codigos de formato de MText: Revit no los entiende
    private static string LimpiarMText(string crudo)
    {
        if (string.IsNullOrEmpty(crudo)) return "";

        string t = crudo;
        t = Regex.Replace(t, @"\\P", "\n");                       //salto de parrafo
        t = Regex.Replace(t, @"\\[LlOoKk]", "");                  //subrayado, tachado
        t = Regex.Replace(t, @"\\[AaCcFfHhQqTtWwpf][^;\\]*;", ""); //altura, fuente, ancho, oblicuidad
        t = Regex.Replace(t, @"\\S([^;^]*)[\^/#]([^;]*);", "$1/$2"); //fracciones apiladas
        t = t.Replace("%%c", "Ø").Replace("%%C", "Ø")
             .Replace("%%d", "°").Replace("%%D", "°")
             .Replace("%%p", "±").Replace("%%P", "±");
        t = t.Replace("{", "").Replace("}", "");
        t = t.Replace(@"\\", "\\");
        return t.Trim();
    }

    // =================================================================================
    //  Escritura del dwg de la vista
    // =================================================================================

    private static void Escribir(Database db, string ruta, List<ObjectId> ids, Candidato c)
    {
        //WblockCloneObjects arrastra solo lo que haga falta de las tablas de simbolos
        //(capas, tipos de linea, estilos de texto): clonar a mano deja referencias colgando.
        using (var dbNuevo = new Database(true, true))
        {
            dbNuevo.Insunits = db.Insunits;

            ObjectId msNuevoId;
            using (Transaction trN = dbNuevo.TransactionManager.StartTransaction())
            {
                var btN = (BlockTable)trN.GetObject(dbNuevo.BlockTableId, OpenMode.ForRead);
                msNuevoId = btN[BlockTableRecord.ModelSpace];
                trN.Commit();
            }

            var coleccion = new ObjectIdCollection(ids.ToArray());
            var mapa = new IdMapping();
            db.WblockCloneObjects(coleccion, msNuevoId, mapa, DuplicateRecordCloning.Replace, false);

            //Recentrar en el origen y deshacer el giro del viewport. El centro de la ventana
            //pasa a ser (0,0), que es el origen local de la vista de dibujo en Revit.
            Matrix3d aOrigen = Matrix3d.Displacement(new Vector3d(-c.CentroX, -c.CentroY, 0));
            //Primero trasladar y despues girar: A.PostMultiplyBy(B) da A*B, que aplica B antes que A
            Matrix3d matriz = Math.Abs(c.Giro) > 1e-12
                ? Matrix3d.Rotation(-c.Giro, Vector3d.ZAxis, Point3d.Origin).PostMultiplyBy(aOrigen)
                : aOrigen;

            using (Transaction trN = dbNuevo.TransactionManager.StartTransaction())
            {
                foreach (ObjectId origen in coleccion)
                {
                    if (!mapa.Contains(origen)) continue;
                    try
                    {
                        var ent = trN.GetObject(mapa[origen].Value, OpenMode.ForWrite) as AcEntity;
                        if (ent == null) continue;

                        ent.TransformBy(matriz);

                        //Z a cero: aplastar una linea que esta a Z=5 es inocuo, y a Revit le hace
                        //falta porque NewDetailCurve exige la curva en el plano de la vista
                        Aplanar(ent);
                    }
                    catch { }
                }
                trN.Commit();
            }

            dbNuevo.SaveAs(ruta, DwgVersion.Current);
        }
    }

    private static void Aplanar(AcEntity ent)
    {
        var linea = ent as Line;
        if (linea != null)
        {
            linea.StartPoint = new Point3d(linea.StartPoint.X, linea.StartPoint.Y, 0);
            linea.EndPoint = new Point3d(linea.EndPoint.X, linea.EndPoint.Y, 0);
            return;
        }

        var pl = ent as Polyline;
        if (pl != null) { pl.Elevation = 0; return; }

        var arco = ent as Arc;
        if (arco != null) { arco.Center = new Point3d(arco.Center.X, arco.Center.Y, 0); return; }

        var circulo = ent as Circle;
        if (circulo != null) { circulo.Center = new Point3d(circulo.Center.X, circulo.Center.Y, 0); return; }

        //Splines y polilineas 3D: aplastarlas aqui cambiaria su forma. Se dejan y el lado de
        //Revit las aplana contando cuantas se deformaron, para poder revisarlas.
    }

    // =================================================================================
    //  Manifiesto
    // =================================================================================

    private static string Manifiesto(string codigo, string titulo, string archivo, string layout,
        double papelAnchoMm, double papelAltoMm, List<Vista> vistas, List<string> avisos)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"codigo\": {Cadena(codigo)},");
        sb.AppendLine($"  \"titulo\": {Cadena(titulo)},");
        sb.AppendLine($"  \"archivoOrigen\": {Cadena(archivo)},");
        sb.AppendLine($"  \"layout\": {Cadena(layout)},");
        sb.AppendLine($"  \"papelAnchoMm\": {Num(papelAnchoMm)},");
        sb.AppendLine($"  \"papelAltoMm\": {Num(papelAltoMm)},");

        sb.AppendLine("  \"avisos\": [");
        for (int i = 0; i < avisos.Count; i++)
            sb.AppendLine($"    {Cadena(avisos[i])}{(i + 1 < avisos.Count ? "," : "")}");
        sb.AppendLine("  ],");

        sb.AppendLine("  \"vistas\": [");
        for (int i = 0; i < vistas.Count; i++)
        {
            Vista v = vistas[i];
            sb.AppendLine("    {");
            sb.AppendLine($"      \"nombre\": {Cadena(v.Nombre)},");
            sb.AppendLine($"      \"handle\": {Cadena(v.Handle)},");
            sb.AppendLine($"      \"dwg\": {Cadena(v.Dwg)},");
            sb.AppendLine($"      \"escalaDenominador\": {v.EscalaDenominador},");
            sb.AppendLine($"      \"customScale\": {Num(v.CustomScale)},");
            sb.AppendLine($"      \"ventanaAnchoM\": {Num(v.VentanaAnchoM)},");
            sb.AppendLine($"      \"ventanaAltoM\": {Num(v.VentanaAltoM)},");
            sb.AppendLine($"      \"ventanaCentroX\": {Num(v.VentanaCentroX)},");
            sb.AppendLine($"      \"ventanaCentroY\": {Num(v.VentanaCentroY)},");
            sb.AppendLine($"      \"papelCentroXMm\": {Num(v.PapelCentroXMm)},");
            sb.AppendLine($"      \"papelCentroYMm\": {Num(v.PapelCentroYMm)},");
            sb.AppendLine("      \"textos\": [");
            for (int j = 0; j < v.Textos.Count; j++)
            {
                Texto t = v.Textos[j];
                sb.Append("        { ");
                sb.Append($"\"texto\": {Cadena(t.Contenido)}, ");
                sb.Append($"\"xM\": {Num(t.XM)}, \"yM\": {Num(t.YM)}, ");
                sb.Append($"\"alturaMm\": {Num(t.AlturaMm)}, ");
                sb.Append($"\"rotacionGrados\": {Num(t.RotacionGrados)}, ");
                sb.Append($"\"capa\": {Cadena(t.Capa)}");
                sb.AppendLine($" }}{(j + 1 < v.Textos.Count ? "," : "")}");
            }
            sb.AppendLine("      ]");
            sb.AppendLine($"    }}{(i + 1 < vistas.Count ? "," : "")}");
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");

        return sb.ToString();
    }

    //Precision completa a proposito: el centro de la ventana es lo que permite reconstruir
    //la coordenada real, y redondearlo a centimetros mete ese error en la reconstruccion.
    private static string Num(double valor) =>
        double.IsNaN(valor) || double.IsInfinity(valor) ? "0" : valor.ToString("R", CultureInfo.InvariantCulture);

    private static string Cadena(string valor)
    {
        if (valor == null) return "\"\"";
        var sb = new StringBuilder("\"");
        foreach (char c in valor)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    // =================================================================================
    //  Unidades
    // =================================================================================

    private static double MmPorUnidad(UnitsValue unidades)
    {
        switch (unidades)
        {
            case UnitsValue.Millimeters: return 1.0;
            case UnitsValue.Centimeters: return 10.0;
            case UnitsValue.Decimeters: return 100.0;
            case UnitsValue.Meters: return 1000.0;
            case UnitsValue.Inches: return 25.4;
            case UnitsValue.Feet: return 304.8;
            default: return 0.0;   //sin unidades: se para, no se adivina
        }
    }

    private static double PapelMm(Layout lay) => lay.PlotPaperUnits == PlotPaperUnit.Inches ? 25.4 : 1.0;

    private static void TamanoPapel(Layout lay, out double anchoMm, out double altoMm)
    {
        double factor = PapelMm(lay);
        Point2d tamano = lay.PlotPaperSize;
        anchoMm = tamano.X * factor;
        altoMm = tamano.Y * factor;

        if (lay.PlotRotation == PlotRotation.Degrees090 || lay.PlotRotation == PlotRotation.Degrees270)
        {
            double t = anchoMm; anchoMm = altoMm; altoMm = t;
        }
    }

    // =================================================================================
    //  Tipos auxiliares
    // =================================================================================

    private sealed class Candidato
    {
        public string Nombre = "";
        public string Handle = "";
        public double CustomScale;      //unidades de papel por unidad de modelo
        public double CustomScaleMm;    //mm de papel por unidad de modelo
        public double Giro;
        public double AnchoM, AltoM;
        public double CentroX, CentroY;
        public double SeleccionAncho, SeleccionAlto;
        public double PapelCentroX, PapelCentroY;
        public List<ObjectId> CapasCongeladas = new List<ObjectId>();
    }

    private sealed class Vista
    {
        public string Nombre = "", Handle = "", Dwg = "";
        public int EscalaDenominador;
        public double CustomScale;
        public double VentanaAnchoM, VentanaAltoM, VentanaCentroX, VentanaCentroY;
        public double PapelCentroXMm, PapelCentroYMm;
        public int Entidades;
        public List<Texto> Textos = new List<Texto>();
    }

    private sealed class Texto
    {
        public string Contenido = "", Capa = "";
        public double XM, YM, AlturaMm, RotacionGrados;
    }
}
