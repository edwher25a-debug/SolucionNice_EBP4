using ProyectoNice.Models;

namespace ProyectoNice.Utils
{
    /// <summary>
    ///     Pasa el contenido de un dwg de una vista a geometria nativa de Revit.
    ///
    ///     El dwg se vincula "solo en esta vista", se lee su geometria, se crean lineas de
    ///     detalle con el estilo que mande la tabla de capas, y despues se borra el vinculo
    ///     y su tipo. El resultado no depende del dwg: responde a la escala de la vista,
    ///     se puede editar y no infla el proyecto.
    ///
    ///     Los textos NO salen del dwg: la API de Revit no expone el texto de un CAD
    ///     importado, asi que viajan en el manifiesto y se crean como TextNote.
    /// </summary>
    public static class ConversorCAD
    {
        public static double Metros(double metros) => UnitUtils.ConvertToInternalUnits(metros, UnitTypeId.Meters);
        public static double Mm(double milimetros) => UnitUtils.ConvertToInternalUnits(milimetros, UnitTypeId.Millimeters);
        public static double AMm(double interno) => UnitUtils.ConvertFromInternalUnits(interno, UnitTypeId.Millimeters);

        private const double TolZ = 1e-7;

        /// <summary>
        ///     Rellena una vista de dibujo ya creada. Devuelve la reconciliacion de lo que entro y salio.
        ///     Se llama dentro de una SubTransaction: si lanza, el paso se deshace solo.
        /// </summary>
        public static m_ReporteVista Rellenar(Document doc, ViewDrafting vista, m_VistaCAD datos, string rutaDwg,
            Dictionary<string, GraphicsStyle> mapeoCapas, GraphicsStyle estiloSinMapear, List<TextNoteType> tiposTexto)
        {
            var reporte = new m_ReporteVista { Vista = datos.Nombre };

            //01_Lo que hubiera de una pasada anterior: se borra y se reconstruye, nunca se duplica
            BorrarContenido(doc, vista);

            //02_Geometria desde el dwg vinculado
            if (!System.IO.File.Exists(rutaDwg)) throw new System.IO.FileNotFoundException($"no esta el dwg de la vista: {rutaDwg}");

            var opcionesVinculo = new DWGImportOptions
            {
                ThisViewOnly = true,
                Placement = ImportPlacement.Origin,      //el dwg ya viene recentrado en el origen
                Unit = ImportUnit.Meter,
                ColorMode = ImportColorMode.BlackAndWhite,
                AutoCorrectAlmostVHLines = false,        //no queremos que enderece nada por su cuenta
                OrientToView = false
            };

            if (!doc.Link(rutaDwg, opcionesVinculo, vista, out ElementId idVinculo) || idVinculo == ElementId.InvalidElementId)
                throw new InvalidOperationException("Revit no pudo vincular el dwg de la vista");

            var instancia = doc.GetElement(idVinculo) as ImportInstance;
            if (instancia == null) throw new InvalidOperationException("el vinculo no devolvio un ImportInstance");

            var piezas = new List<Pieza>();
            GeometryElement geometria = instancia.get_Geometry(new Options
            {
                ComputeReferences = false,
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Fine
            });
            if (geometria != null) Recorrer(geometria, piezas);

            //03_Borrar el vinculo y su tipo antes de crear lo nativo.
            //   Si no se borra el tipo, treinta planos dejan doscientos CADLinkType en el proyecto.
            ElementId idTipoVinculo = instancia.GetTypeId();
            doc.Delete(instancia.Id);
            if (idTipoVinculo != null && idTipoVinculo != ElementId.InvalidElementId)
            {
                try { doc.Delete(idTipoVinculo); } catch { /* lo usa otro vinculo: se queda */ }
            }

            //04_Lineas de detalle
            double tolCorta = doc.Application.ShortCurveTolerance;
            reporte.CurvasLeidas = piezas.Count;

            foreach (Pieza pieza in piezas)
            {
                if (pieza.Curva == null) { reporte.ObjetosNoSoportados++; continue; }

                List<Curve> aplanadas = Aplanar(pieza.Curva, tolCorta, reporte);
                if (aplanadas.Count == 0) { reporte.CurvasDescartadas++; continue; }

                GraphicsStyle estilo = EstiloDeCapa(doc, pieza.IdEstilo, mapeoCapas, estiloSinMapear, reporte);

                int creadasAqui = 0;
                foreach (Curve curva in aplanadas)
                {
                    try
                    {
                        DetailCurve detalle = doc.Create.NewDetailCurve(vista, curva);
                        if (estilo != null) detalle.LineStyle = estilo;
                        creadasAqui++;
                    }
                    catch
                    {
                        //Curva que Revit rechaza (degenerada, autointersecante): se cuenta, no se pierde en silencio
                    }
                }

                if (creadasAqui > 0) { reporte.CurvasConvertidas++; reporte.LineasCreadas += creadasAqui; }
                else reporte.CurvasDescartadas++;
            }

            //05_Textos desde el manifiesto
            reporte.TextosLeidos = datos.Textos.Count;
            reporte.TextosCreados = CrearTextos(doc, vista, datos, tiposTexto, reporte);

            return reporte;
        }

        /// <summary>Recorte igual a la ventana del viewport: es el equivalente del borde del viewport.</summary>
        public static void AjustarRecorte(ViewDrafting vista, m_VistaCAD datos)
        {
            double anchoFt = Metros(datos.VentanaAnchoM);
            double altoFt = Metros(datos.VentanaAltoM);
            if (anchoFt <= 0 || altoFt <= 0) return;

            BoundingBoxXYZ recorte = vista.CropBox;
            recorte.Min = new XYZ(-anchoFt / 2, -altoFt / 2, -1);
            recorte.Max = new XYZ(anchoFt / 2, altoFt / 2, 1);
            vista.CropBox = recorte;
            vista.CropBoxActive = true;
            vista.CropBoxVisible = false;

            //El recorte de anotacion oculta cualquier texto que no quepa entero dentro del recorte.
            //En AutoCAD esos textos si se veian, asi que se desactiva.
            Parameter recorteAnotacion = vista.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
            if (recorteAnotacion != null && !recorteAnotacion.IsReadOnly) recorteAnotacion.Set(0);
        }

        /// <summary>Deja la vista vacia para reconstruirla: solo lo que crea esta herramienta.</summary>
        public static void BorrarContenido(Document doc, View vista)
        {
            var aBorrar = new List<ElementId>();
            aBorrar.AddRange(new FilteredElementCollector(doc, vista.Id).OfClass(typeof(CurveElement)).ToElementIds());
            aBorrar.AddRange(new FilteredElementCollector(doc, vista.Id).OfClass(typeof(TextNote)).ToElementIds());
            aBorrar.AddRange(new FilteredElementCollector(doc, vista.Id).OfClass(typeof(ImportInstance)).ToElementIds());
            if (aBorrar.Count > 0) doc.Delete(aBorrar);
        }

        //=== Geometria ===

        private sealed class Pieza
        {
            public Curve Curva;
            public ElementId IdEstilo;
        }

        //GetInstanceGeometry ya devuelve la geometria transformada, asi que no hay que arrastrar matrices
        private static void Recorrer(GeometryElement geometria, List<Pieza> salida)
        {
            foreach (GeometryObject objeto in geometria)
            {
                if (objeto is GeometryInstance instancia)
                {
                    GeometryElement interior = instancia.GetInstanceGeometry();
                    if (interior != null) Recorrer(interior, salida);
                    continue;
                }

                if (objeto is Curve curva)
                {
                    salida.Add(new Pieza { Curva = curva, IdEstilo = objeto.GraphicsStyleId });
                    continue;
                }

                //El CAD importado suele llegar como PolyLine, no como Curve
                if (objeto is PolyLine polilinea)
                {
                    IList<XYZ> puntos = polilinea.GetCoordinates();
                    for (int i = 0; i + 1 < puntos.Count; i++)
                    {
                        try { salida.Add(new Pieza { Curva = Line.CreateBound(puntos[i], puntos[i + 1]), IdEstilo = objeto.GraphicsStyleId }); }
                        catch { /* dos puntos iguales */ }
                    }
                    continue;
                }

                //Mallas y solidos: no se convierten a linea de detalle. Se cuentan arriba.
                salida.Add(new Pieza { Curva = null, IdEstilo = objeto.GraphicsStyleId });
            }
        }

        private static XYZ Aplastar(XYZ punto) => new XYZ(punto.X, punto.Y, 0);

        /// <summary>
        ///     Aplana la curva a Z=0. Aplastar una linea que esta a Z=5 es inocuo; aplastar una
        ///     spline con Z variable le cambia la forma, y eso se cuenta aparte para poder revisarlo.
        /// </summary>
        private static List<Curve> Aplanar(Curve curva, double tolCorta, m_ReporteVista reporte)
        {
            var salida = new List<Curve>();

            if (curva is Line linea)
            {
                XYZ a = Aplastar(linea.GetEndPoint(0));
                XYZ b = Aplastar(linea.GetEndPoint(1));
                if (a.DistanceTo(b) < tolCorta) { reporte.CurvasCortas++; return salida; }
                salida.Add(Line.CreateBound(a, b));
                return salida;
            }

            if (curva is Arc arco)
            {
                if (!arco.IsBound)
                {
                    if (Math.Abs(Math.Abs(arco.Normal.Z) - 1.0) > 1e-6) reporte.CurvasAplanadas++;
                    try { salida.Add(Arc.Create(Aplastar(arco.Center), arco.Radius, 0, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY)); }
                    catch { reporte.CurvasCortas++; }
                    return salida;
                }

                XYZ p0 = arco.GetEndPoint(0), p2 = arco.GetEndPoint(1), p1 = arco.Evaluate(0.5, true);
                if (Math.Abs(p0.Z - p1.Z) > TolZ || Math.Abs(p1.Z - p2.Z) > TolZ) reporte.CurvasAplanadas++;

                XYZ a = Aplastar(p0), b = Aplastar(p2), medio = Aplastar(p1);
                try { salida.Add(Arc.Create(a, b, medio)); }
                catch
                {
                    //Arco degenerado al aplanarlo: se salva como linea si da la longitud
                    if (a.DistanceTo(b) >= tolCorta) salida.Add(Line.CreateBound(a, b));
                    else reporte.CurvasCortas++;
                }
                return salida;
            }

            //Splines, elipses y demas: se teselan en segmentos
            IList<XYZ> puntos;
            try { puntos = curva.Tessellate(); }
            catch { reporte.CurvasCortas++; return salida; }

            if (puntos.Count >= 2)
            {
                double z0 = puntos[0].Z;
                for (int i = 1; i < puntos.Count; i++)
                {
                    if (Math.Abs(puntos[i].Z - z0) > TolZ) { reporte.CurvasAplanadas++; break; }
                }
            }

            int cortas = 0;
            for (int i = 0; i + 1 < puntos.Count; i++)
            {
                XYZ a = Aplastar(puntos[i]), b = Aplastar(puntos[i + 1]);
                if (a.DistanceTo(b) < tolCorta) { cortas++; continue; }
                salida.Add(Line.CreateBound(a, b));
            }
            if (salida.Count == 0 && cortas > 0) reporte.CurvasCortas++;

            return salida;
        }

        private static GraphicsStyle EstiloDeCapa(Document doc, ElementId idEstilo,
            Dictionary<string, GraphicsStyle> mapeo, GraphicsStyle sinMapear, m_ReporteVista reporte)
        {
            string capa = NombreDeCapa(doc, idEstilo);
            if (capa != "" && mapeo.TryGetValue(capa, out GraphicsStyle estilo)) return estilo;

            //Capa que no esta en la tabla: estilo chillon y al reporte, para verla en el plano al primer vistazo
            reporte.CapasSinMapear.Add(capa == "" ? "(sin capa)" : capa);
            return sinMapear;
        }

        /// <summary>El nombre de la subcategoria de un CAD importado es el nombre de la capa del dwg.</summary>
        public static string NombreDeCapa(Document doc, ElementId idEstilo)
        {
            if (idEstilo == null || idEstilo == ElementId.InvalidElementId) return "";
            var estilo = doc.GetElement(idEstilo) as GraphicsStyle;
            if (estilo == null) return "";
            return estilo.GraphicsStyleCategory?.Name ?? estilo.Name ?? "";
        }

        //=== Textos ===

        private static int CrearTextos(Document doc, View vista, m_VistaCAD datos, List<TextNoteType> tipos, m_ReporteVista reporte)
        {
            int creados = 0;

            foreach (m_TextoCAD texto in datos.Textos)
            {
                if (string.IsNullOrWhiteSpace(texto.Texto)) { creados++; continue; }   //vacio: no hay nada que crear

                TextNoteType tipo = TipoMasCercano(tipos, texto.AlturaMm);
                if (tipo == null)
                {
                    reporte.AlturasSinTipo.Add($"{texto.AlturaMm:0.##} mm");
                    continue;
                }

                var opciones = new TextNoteOptions(tipo.Id)
                {
                    HorizontalAlignment = HorizontalTextAlignment.Left,
                    Rotation = texto.RotacionGrados * Math.PI / 180.0
                };

                try
                {
                    TextNote.Create(doc, vista.Id, new XYZ(Metros(texto.XM), Metros(texto.YM), 0), texto.Texto, opciones);
                    creados++;
                }
                catch
                {
                    //Texto que Revit rechaza: se cuenta en la reconciliacion al no cuadrar
                }
            }

            return creados;
        }

        /// <summary>
        ///     El tipo de texto se elige por la altura en PAPEL, nunca por la altura en el modelo.
        ///     Eso es lo que hace que las dos escalas del dwg (1:20 y 1:25) colapsen en un solo
        ///     juego de tipos y que las vistas con la escala mal puesta queden normalizadas.
        /// </summary>
        public static TextNoteType TipoMasCercano(List<TextNoteType> tipos, double alturaMm, double toleranciaMm = 0.75)
        {
            if (tipos == null || tipos.Count == 0 || alturaMm <= 0) return null;

            TextNoteType mejor = null;
            double menorDiferencia = double.MaxValue;

            foreach (TextNoteType tipo in tipos)
            {
                double alturaTipo = AlturaMm(tipo);
                if (alturaTipo <= 0) continue;

                double diferencia = Math.Abs(alturaTipo - alturaMm);
                if (diferencia < menorDiferencia) { menorDiferencia = diferencia; mejor = tipo; }
            }

            return menorDiferencia <= toleranciaMm ? mejor : null;
        }

        public static double AlturaMm(TextNoteType tipo)
        {
            Parameter tamano = tipo?.get_Parameter(BuiltInParameter.TEXT_SIZE);
            return tamano == null ? 0 : AMm(tamano.AsDouble());
        }
    }
}
