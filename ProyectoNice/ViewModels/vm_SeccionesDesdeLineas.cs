using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using ProyectoNice.Acotado;
using ProyectoNice.Utils;

namespace ProyectoNice.ViewModels
{
    /// <summary>
    ///     Crea una seccion por cada linea de un estilo dado en la planta activa, le aplica
    ///     una plantilla de vista, la acota segun lo que corte (box, pila, estribo...) y la
    ///     coloca en un plano en el primer espacio libre.
    /// </summary>
    public sealed class vm_SeccionesDesdeLineas : vm_Herramienta
    {
        private const string Ninguno = "(ninguno)";

        //Datos de entrada
        public Document doc;
        public Selection seleccion;

        //Lineas guia
        public List<string> ListaEstilosCB { get; }
        public string EstiloSeleccCB { get; set; }

        //Seccion
        public List<ViewFamilyType> ListaTiposSeccionCB { get; }
        public ViewFamilyType TipoSeccionSeleccCB { get; set; }
        public List<string> ListaSentidosCB { get; } = new List<string> { "Derecha de la planta", "Izquierda de la planta", "Arriba de la planta", "Abajo de la planta" };
        public string SentidoSeleccCB { get; set; }
        public double DesfaseTB { get; set; } = 5;
        public double ProfundidadTB { get; set; } = 0.10;

        //Plantilla de vista
        public List<string> ListaPlantillasCB { get; }
        public string PlantillaSeleccCB { get; set; }

        //Acotado
        public bool AcotarCK { get; set; } = true;
        public string EstiloMarcasSeleccCB { get; set; }

        //Plano
        public List<string> ListaPlanosCB { get; }
        public string PlanoSeleccCB { get; set; }

        //Botones
        public RelayCommand AceptarBT { get; set; }

        private readonly List<GraphicsStyle> estilos;
        private readonly List<View> plantillas;
        private readonly List<ViewSheet> planos;

        //Constructor
        public vm_SeccionesDesdeLineas(Document doc, Selection seleccion)
        {
            this.doc = doc;
            this.seleccion = seleccion;

            estilos = MetodoDesarrollo.EstilosDeLinea(doc);
            ListaEstilosCB = estilos.Select(e => e.Name).ToList();
            EstiloSeleccCB = ListaEstilosCB.FirstOrDefault(n => n.IndexOf("seccion", StringComparison.OrdinalIgnoreCase) >= 0) ?? ListaEstilosCB.FirstOrDefault();
            EstiloMarcasSeleccCB = ListaEstilosCB.FirstOrDefault(n => n.IndexOf("invisible", StringComparison.OrdinalIgnoreCase) >= 0) ?? ListaEstilosCB.FirstOrDefault();

            ListaTiposSeccionCB = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .Where(t => t.ViewFamily == ViewFamily.Section).OrderBy(t => t.Name).ToList();
            TipoSeccionSeleccCB = ListaTiposSeccionCB.FirstOrDefault();
            SentidoSeleccCB = ListaSentidosCB[0];

            plantillas = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => v.IsTemplate && v.ViewType == ViewType.Section).OrderBy(v => v.Name).ToList();
            ListaPlantillasCB = new List<string> { Ninguno };
            ListaPlantillasCB.AddRange(plantillas.Select(v => v.Name));
            PlantillaSeleccCB = Ninguno;

            planos = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder).OrderBy(s => s.SheetNumber).ToList();
            ListaPlanosCB = new List<string> { Ninguno };
            ListaPlanosCB.AddRange(planos.Select(NombrePlano));
            PlanoSeleccCB = Ninguno;

            AceptarBT = new RelayCommand(Validar);
        }

        private static string NombrePlano(ViewSheet plano) => $"{plano.SheetNumber} - {plano.Name}";

        public void Validar()
        {
            View vista = doc.ActiveView;
            if (!(vista is ViewPlan))
            {
                Aviso("Secciones desde lineas: abra la planta donde estan dibujadas las lineas.");
                return;
            }

            if (string.IsNullOrEmpty(EstiloSeleccCB) || TipoSeccionSeleccCB == null || ProfundidadTB <= 0)
            {
                Aviso("Secciones desde lineas: elija el estilo de linea, el tipo de seccion y una profundidad mayor que cero.");
                return;
            }

            Ejecutar(Aceptar);
        }

        public void Aceptar()
        {
            ViewPlan planta = (ViewPlan)doc.ActiveView;
            double Pies(double metros) => UnitUtils.ConvertToInternalUnits(metros, UnitTypeId.Meters);

            //01_Lineas rectas del estilo elegido en la planta activa
            List<CurveElement> lineas = new FilteredElementCollector(doc, planta.Id).OfClass(typeof(CurveElement)).Cast<CurveElement>()
                .Where(c => c.LineStyle != null && c.LineStyle.Name == EstiloSeleccCB && c.GeometryCurve is Line)
                .ToList();
            if (lineas.Count == 0)
            {
                Aviso($"Secciones desde lineas: no hay lineas rectas con el estilo \"{EstiloSeleccCB}\" en esta planta.");
                return;
            }

            XYZ haciaDonde = SentidoSeleccCB == ListaSentidosCB[0] ? planta.RightDirection
                : SentidoSeleccCB == ListaSentidosCB[1] ? -planta.RightDirection
                : SentidoSeleccCB == ListaSentidosCB[2] ? planta.UpDirection
                : -planta.UpDirection;

            View plantilla = plantillas.FirstOrDefault(v => v.Name == PlantillaSeleccCB);
            ViewSheet plano = planos.FirstOrDefault(p => NombrePlano(p) == PlanoSeleccCB);
            GraphicsStyle estiloMarcas = estilos.FirstOrDefault(e => e.Name == EstiloMarcasSeleccCB);

            var creadas = new List<string>();
            var avisos = new List<string>();
            ViewSection ultima = null;

            using (Transaction transaccion = new Transaction(doc, "Secciones desde lineas"))
            {
                transaccion.Start();

                foreach (CurveElement linea in lineas)
                {
                    using (SubTransaction paso = new SubTransaction(doc))
                    {
                        paso.Start();
                        try
                        {
                            string detalle = CrearSeccion(planta, linea, haciaDonde, plantilla, estiloMarcas, plano, Pies, out ViewSection seccion);
                            paso.Commit();
                            ultima = seccion;
                            creadas.Add(seccion.Name);
                            if (detalle != "") avisos.Add($"{seccion.Name}: {detalle}");
                        }
                        catch (Exception ex)
                        {
                            if (!paso.HasEnded()) paso.RollBack();
                            avisos.Add($"linea {linea.Id}: {ex.Message}");
                        }
                    }
                }

                if (creadas.Count > 0) transaccion.Commit();
                else transaccion.RollBack();
            }

            if (ultima != null) new UIDocument(doc).ActiveView = ultima;

            string resumen = $"Secciones desde lineas: {creadas.Count} de {lineas.Count} creada(s)";
            if (creadas.Count > 0) resumen += $" ({string.Join(", ", creadas)})";
            if (avisos.Count > 0) resumen += ". " + string.Join(" | ", avisos);
            Aviso(resumen);
            if (avisos.Count > 0) TaskDialog.Show("Secciones desde lineas", resumen);
        }

        private string CrearSeccion(ViewPlan planta, CurveElement linea, XYZ haciaDonde, View plantilla,
            GraphicsStyle estiloMarcas, ViewSheet plano, Func<double, double> Pies, out ViewSection seccion)
        {
            var avisos = new List<string>();
            Curve curva = linea.GeometryCurve;
            XYZ p0 = curva.GetEndPoint(0), p1 = curva.GetEndPoint(1);
            XYZ direccion = new XYZ(p1.X - p0.X, p1.Y - p0.Y, 0);
            if (direccion.GetLength() < doc.Application.ShortCurveTolerance) throw new InvalidOperationException("la linea es demasiado corta");
            direccion = direccion.Normalize();

            //La seccion mira hacia el lado pedido, perpendicular a la linea
            XYZ mira = new XYZ(direccion.Y, -direccion.X, 0);
            if (mira.DotProduct(haciaDonde) < 0) mira = -mira;
            if (Math.Abs(mira.DotProduct(haciaDonde)) < 0.2)
                avisos.Add("la linea es casi paralela al sentido elegido; se tomo el lado mas cercano");

            //Elementos que cruza la linea, para dimensionar la caja de la seccion
            double holgura = Pies(1);
            var contorno = new Outline(
                new XYZ(Math.Min(p0.X, p1.X) - holgura, Math.Min(p0.Y, p1.Y) - holgura, -1e6),
                new XYZ(Math.Max(p0.X, p1.X) + holgura, Math.Max(p0.Y, p1.Y) + holgura, 1e6));
            List<Element> cruzados = new FilteredElementCollector(doc, planta.Id).WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(contorno))
                .Where(e => !(e is CurveElement) && e.Category != null && e.Category.CategoryType == CategoryType.Model &&
                            e.Category.Id.Value != (long)BuiltInCategory.OST_Cameras)
                .ToList();

            double zMin = cruzados.Select(e => e.get_BoundingBox(null)).Where(b => b != null).Select(b => b.Min.Z).DefaultIfEmpty(-Pies(5)).Min();
            double zMax = cruzados.Select(e => e.get_BoundingBox(null)).Where(b => b != null).Select(b => b.Max.Z).DefaultIfEmpty(Pies(5)).Max();
            zMin = Math.Max(zMin, planta.GenLevel != null ? planta.GenLevel.ProjectElevation - Pies(100) : zMin);

            XYZ medio = (p0 + p1) / 2 + mira * Pies(DesfaseTB / 100.0);
            var transformada = Transform.Identity;
            transformada.Origin = new XYZ(medio.X, medio.Y, 0);
            transformada.BasisZ = mira;
            transformada.BasisY = XYZ.BasisZ;
            transformada.BasisX = XYZ.BasisZ.CrossProduct(mira);

            double mitad = p0.DistanceTo(p1) / 2;
            var caja = new BoundingBoxXYZ
            {
                Transform = transformada,
                Min = new XYZ(-mitad, zMin - Pies(1), 0),
                Max = new XYZ(mitad, zMax + Pies(1), Pies(ProfundidadTB))
            };

            seccion = ViewSection.CreateSection(doc, TipoSeccionSeleccCB.Id, caja);
            if (plantilla != null) seccion.ViewTemplateId = plantilla.Id;
            doc.Regenerate();

            //Recorte ajustado a lo que corta, con espacio para las cotas
            Caja2D? cortado = ExtensionCortada(seccion, cruzados);
            if (cortado.HasValue) AjustarRecorte(seccion, cortado.Value, Pies);
            else avisos.Add("la linea no corta ningun elemento");

            //Cotas segun el tipo de vista que se reconozca
            if (AcotarCK && cortado.HasValue)
            {
                OpcionesAcotado opciones = OpcionesAcotado.PorDefecto(doc);
                opciones.EstiloMarcas = estiloMarcas ?? opciones.EstiloMarcas;
                ResultadoAcotado acotado = Acotador.Acotar(seccion, opciones);
                avisos.AddRange(acotado.Avisos.Distinct());
            }

            //Plano
            if (plano != null)
            {
                string colocacion = ColocarEnPlano(plano, seccion);
                if (colocacion != "") avisos.Add(colocacion);
            }

            return string.Join("; ", avisos);
        }

        //Contorno, en coordenadas de la vista, de lo que corta la seccion dentro de su ancho
        private static Caja2D? ExtensionCortada(ViewSection seccion, IEnumerable<Element> candidatos)
        {
            var lienzo = new Lienzo(seccion, new OpcionesAcotado());
            List<XYZ> puntos = lienzo.Cortar(candidatos.SelectMany(MetodoDesarrollo.ObtenerSolidos))
                .SelectMany(t => t.Puntos)
                .ToList();
            return puntos.Count == 0 ? (Caja2D?)null : Caja2D.De(puntos);
        }

        //Recorte al contorno cortado mas margenes: a los lados y abajo para las cotas, arriba tambien para las pendientes
        private static void AjustarRecorte(ViewSection seccion, Caja2D cortado, Func<double, double> Pies)
        {
            var lienzo = new Lienzo(seccion, new OpcionesAcotado());
            BoundingBoxXYZ recorte = seccion.CropBox;
            Transform aLocal = recorte.Transform.Inverse;
            XYZ a = aLocal.OfPoint(lienzo.AModelo(cortado.X0 - Pies(1.2), cortado.Y0 - Pies(1.0)));
            XYZ b = aLocal.OfPoint(lienzo.AModelo(cortado.X1 + Pies(1.2), cortado.Y1 + Pies(2.0)));
            recorte.Min = new XYZ(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), recorte.Min.Z);
            recorte.Max = new XYZ(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), recorte.Max.Z);
            seccion.CropBox = recorte;
            seccion.CropBoxActive = true;
            seccion.Document.Regenerate();
        }

        //Coloca la vista siempre: en el primer hueco libre (junto a una vista ya colocada o recorriendo el plano
        //por filas) o, si la seccion no cabe en ninguno, donde menos tape. Devuelve el aviso, o vacio si quedo libre
        private string ColocarEnPlano(ViewSheet plano, View vista)
        {
            if (!Viewport.CanAddViewToSheet(doc, plano.Id, vista.Id)) return $"la vista ya esta en otro plano y no se coloco en {plano.SheetNumber}";

            double Mm(double milimetros) => UnitUtils.ConvertToInternalUnits(milimetros, UnitTypeId.Millimeters);
            List<Outline> ocupados = plano.GetAllViewports().Select(id => ((Viewport)doc.GetElement(id)).GetBoxOutline()).ToList();

            //Las tablas (coordenadas, revisiones) tambien ocupan sitio en el plano
            foreach (ScheduleSheetInstance tabla in new FilteredElementCollector(doc, plano.Id).OfClass(typeof(ScheduleSheetInstance)))
            {
                BoundingBoxXYZ caja = tabla.get_BoundingBox(plano);
                if (caja != null) ocupados.Add(new Outline(caja.Min, caja.Max));
            }

            //Limites: el cajetin, o una hoja A0 si el plano no tiene
            Element? cajetin = new FilteredElementCollector(doc, plano.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).FirstElement();
            BoundingBoxXYZ? hoja = cajetin?.get_BoundingBox(plano);
            double margen = Mm(10);
            double minX = hoja != null ? hoja.Min.X + margen : 0, maxX = hoja != null ? hoja.Max.X - margen : Mm(1189);
            double minY = hoja != null ? hoja.Min.Y + margen : 0, maxY = hoja != null ? hoja.Max.Y - margen : Mm(841);

            Viewport nuevo = Viewport.Create(doc, plano.Id, vista.Id, new XYZ((minX + maxX) / 2, (minY + maxY) / 2, 0));
            doc.Regenerate();
            Outline propio = nuevo.GetBoxOutline();
            double ancho = propio.MaximumPoint.X - propio.MinimumPoint.X;
            double alto = propio.MaximumPoint.Y - propio.MinimumPoint.Y;
            double separacion = Mm(10);

            //Area de la vista, con su separacion, que cae sobre otras vistas o tablas o fuera de la hoja
            double Solape(XYZ esquina)
            {
                double x0 = esquina.X - separacion / 2, y0 = esquina.Y - separacion / 2;
                double x1 = esquina.X + ancho + separacion / 2, y1 = esquina.Y + alto + separacion / 2;
                double Cruce(double a0, double a1, double b0, double b1) => Math.Max(0, Math.Min(a1, b1) - Math.Max(a0, b0));
                double dentroDeLaHoja = Cruce(x0, x1, minX, maxX) * Cruce(y0, y1, minY, maxY);
                return ocupados.Sum(o => Cruce(x0, x1, o.MinimumPoint.X, o.MaximumPoint.X) * Cruce(y0, y1, o.MinimumPoint.Y, o.MaximumPoint.Y))
                       + (x1 - x0) * (y1 - y0) - dentroDeLaHoja;
            }

            IEnumerable<XYZ> junto = ocupados.OrderBy(o => o.MinimumPoint.Y).ThenBy(o => o.MinimumPoint.X)
                .Select(o => new XYZ(o.MaximumPoint.X + separacion, o.MinimumPoint.Y, 0));
            IEnumerable<XYZ> rejilla =
                from y in Pasos(minY, Math.Max(minY, maxY - alto), Mm(10))
                from x in Pasos(minX, Math.Max(minX, maxX - ancho), Mm(10))
                select new XYZ(x, y, 0);
            List<XYZ> candidatos = junto.Concat(rejilla).ToList();

            XYZ? libre = candidatos.FirstOrDefault(c => Solape(c) < 1e-9);
            XYZ esquina = libre ?? candidatos.OrderBy(Solape).First();
            nuevo.SetBoxCenter(new XYZ(esquina.X + ancho / 2, esquina.Y + alto / 2, 0));

            return libre != null ? "" : $"no habia un hueco libre en el plano {plano.SheetNumber}: la vista quedo donde menos tapa, revisela";
        }

        private static IEnumerable<double> Pasos(double desde, double hasta, double paso) =>
            Enumerable.Range(0, (int)Math.Floor((hasta - desde) / paso) + 1).Select(i => desde + i * paso);
    }
}
