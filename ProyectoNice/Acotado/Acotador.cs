namespace ProyectoNice.Acotado
{
    public sealed class ResultadoAcotado
    {
        public ResultadoAcotado(View vista, TipoVista tipo)
        {
            Vista = vista.Name;
            Tipo = tipo;
        }

        public string Vista { get; }
        public TipoVista Tipo { get; }
        public int Cotas { get; set; }
        public int Niveles { get; set; }
        public int Etiquetas { get; set; }
        public int Diametros { get; set; }
        public List<string> Avisos { get; } = new List<string>();

        public string Resumen =>
            $"{Vista} ({Tipo}): {Cotas} cotas, {Niveles} niveles, {Etiquetas} etiquetas, {Diametros} diametros" +
            (Avisos.Count > 0 ? ". " + string.Join(" | ", Avisos.Distinct()) : "");
    }

    /// <summary>Acota una vista: reconoce lo que muestra, elige la regla y crea las anotaciones. Requiere una transaccion abierta.</summary>
    public static class Acotador
    {
        public static ResultadoAcotado Acotar(View vista, OpcionesAcotado opciones)
        {
            var lienzo = new Lienzo(vista, opciones);
            var conjunto = new Conjunto(lienzo);
            string? recorte = RecortarPilotes(lienzo, conjunto);
            if (recorte != null)
            {
                lienzo = new Lienzo(vista, opciones);
                conjunto = new Conjunto(lienzo);
            }

            PerfilBox? perfil = vista is ViewSection ? PerfilBox.De(conjunto) : null;
            TipoVista tipo = opciones.Forzar != TipoVista.Automatico ? opciones.Forzar : Reglas.Detectar(conjunto, perfil != null);
            PlanDeAcotado? plan = tipo == TipoVista.Box ? perfil?.Plan() : Reglas.Plan(tipo, conjunto);

            var resultado = new ResultadoAcotado(vista, tipo);
            if (recorte != null) resultado.Avisos.Add(recorte);
            if (plan == null)
            {
                resultado.Avisos.Add(tipo == TipoVista.Box ? "la vista no corta un box de dos celdas" : "no hay una regla para esta vista");
                return resultado;
            }

            Document doc = vista.Document;
            List<Fila> filas = Colocar(plan.Filas, conjunto.Contenido, lienzo).ToList();
            resultado.Cotas = filas.Count(fila => Intentar(doc, resultado, "cota", () => lienzo.Cota(fila)));

            int fuera = filas.Count(fila => FueraDelRecorte(fila, lienzo.Recorte));
            if (fuera > 0) resultado.Avisos.Add($"{fuera} cota(s) quedan fuera del recorte de la vista: amplie el recorte para verlas");

            if (opciones.Niveles)
                resultado.Niveles = plan.Niveles
                    .Count(nivel => Intentar(doc, resultado, "nivel", () => lienzo.Nivel(nivel.Cara, nivel.Punto)));

            if (opciones.Etiquetas)
                resultado.Etiquetas = plan.Etiquetas
                    .Count(p => Intentar(doc, resultado, "etiqueta", () => Etiquetar(lienzo, p)));

            if (opciones.Diametros) resultado.Diametros = Diametros(doc, vista, plan.Diametros, resultado);

            if (plan.Dibujo != null) Intentar(doc, resultado, "dibujo", () => plan.Dibujo(lienzo));
            return resultado;
        }

        //Apila las filas de cada lado desde el contenido hacia afuera: las de mas tramos, mas cerca; el total, al final
        private static IEnumerable<Fila> Colocar(IEnumerable<Fila> filas, Caja2D contenido, Lienzo lienzo) =>
            filas.Where(f => f.EsValida)
                .GroupBy(f => f.Firma).Select(g => g.First())
                .GroupBy(f => f.Lado)
                .SelectMany(lado => lado.Key == null
                    ? lado
                    : lado.OrderByDescending(f => f.Estaciones.Count).Select((fila, i) => fila.Colocada(
                        contenido.Borde(lado.Key.Value) + HaciaAfuera(lado.Key.Value) *
                        lienzo.Papel(lienzo.Opciones.PrimeraFila + i * lienzo.Opciones.EntreFilas))))
                .ToList();

        private static double HaciaAfuera(Lado lado) => lado == Lado.Abajo || lado == Lado.Izquierda ? -1 : 1;

        //En cortes y alzados, los pilotes se dejan ver solo unos metros bajo la base, como en los planos del
        //proyecto: con decenas de metros de pilote la vista no cabe en ningun plano. Devuelve el aviso o nulo
        private static string? RecortarPilotes(Lienzo lienzo, Conjunto c)
        {
            if (!(lienzo.Vista is ViewSection) || !lienzo.Vista.CropBoxActive || lienzo.Opciones.PiloteVisible <= 0 || !c.Hay(Rol.Pilote)) return null;

            List<Estacion> alturasBase = c.Extremos(c.Base, Eje.Y).ToList();
            if (alturasBase.Count == 0) return null;

            double visible = UnitUtils.ConvertToInternalUnits(lienzo.Opciones.PiloteVisible, UnitTypeId.Meters);
            double limite = alturasBase[0].Posicion - visible;
            if (lienzo.Recorte.Y0 >= limite - 1e-6) return null;

            lienzo.RecortarAbajo(limite);
            lienzo.Doc.Regenerate();
            return $"se recorto la vista {lienzo.Opciones.PiloteVisible:0.##} m bajo la base para cortar los pilotes";
        }

        private static bool FueraDelRecorte(Fila fila, Caja2D recorte) =>
            fila.Coordenada is double linea &&
            (fila.Medida == Eje.X ? linea < recorte.Y0 || linea > recorte.Y1 : linea < recorte.X0 || linea > recorte.X1);

        private static void Etiquetar(Lienzo lienzo, Pieza pieza)
        {
            try
            {
                IndependentTag.Create(lienzo.Doc, lienzo.Vista.Id, new Reference(pieza.Elemento), false,
                    TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal,
                    lienzo.AModelo(pieza.Caja.Centro(Eje.X), pieza.Caja.Centro(Eje.Y)));
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                throw new InvalidOperationException($"no hay una etiqueta de {pieza.Elemento.Category.Name} cargada en el proyecto");
            }
        }

        private static int Diametros(Document doc, View vista, List<Reference> arcos, ResultadoAcotado resultado)
        {
#if REVIT2025_OR_GREATER
            return arcos.Count(arco => Intentar(doc, resultado, "diametro", () => RadialDimension.Create(doc, vista, arco, true)));
#else
            if (arcos.Count > 0) resultado.Avisos.Add("Revit 2024 no permite crear cotas de diametro desde un complemento");
            return 0;
#endif
        }

        //Cada anotacion en su subtransaccion: si Revit rechaza una, las demas siguen
        private static bool Intentar(Document doc, ResultadoAcotado resultado, string que, Action accion)
        {
            using (var paso = new SubTransaction(doc))
            {
                paso.Start();
                try
                {
                    accion();
                    paso.Commit();
                    return true;
                }
                catch (Exception ex)
                {
                    if (!paso.HasEnded()) paso.RollBack();
                    resultado.Avisos.Add($"{que}: {ex.Message}");
                    return false;
                }
            }
        }
    }
}
