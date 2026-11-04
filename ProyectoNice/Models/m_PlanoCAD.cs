namespace ProyectoNice.Models
{
    /// <summary>
    ///     Un plano leido de un manifiesto producido por el lado de AutoCAD (TrocearPlanos).
    ///     El manifiesto es el contrato entre los dos lados: AutoCAD escribe, Revit lee.
    /// </summary>
    public sealed class m_PlanoCAD
    {
        public string Codigo { get; set; } = "";          //PL-ES-F1-015
        public string Titulo { get; set; } = "";          //ARMADO ESTRIBO 1 - EJE 1
        public string ArchivoOrigen { get; set; } = "";   //nombre del dwg del que salio
        public string Layout { get; set; } = "";          //PL-15
        public double PapelAnchoMm { get; set; }
        public double PapelAltoMm { get; set; }

        //Carpeta donde estan el manifiesto y los dwg de las vistas
        public string Carpeta { get; set; } = "";
        public string RutaManifiesto { get; set; } = "";

        public List<m_VistaCAD> Vistas { get; set; } = new List<m_VistaCAD>();

        //Capas que el lado de AutoCAD declara haber exportado, con cuantas entidades.
        //Viene en el manifiesto porque las capas de la geometria no se conocen en Revit
        //hasta vincular el dwg, y la tabla de mapeo hay que poder llenarla antes.
        public Dictionary<string, int> Capas { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        //Avisos que ya dejo el lado de AutoCAD (viewports girados, no soportados, etc.)
        public List<string> Avisos { get; set; } = new List<string>();

        public override string ToString() => $"{Codigo} - {Titulo} ({Vistas.Count} vista(s))";
    }

    /// <summary>
    ///     Una vista: el contenido de un viewport de AutoCAD, ya recortado y recentrado.
    ///     La geometria viaja en el dwg; los textos viajan aqui porque la API de Revit
    ///     no expone el texto de un CAD importado.
    /// </summary>
    public sealed class m_VistaCAD
    {
        public string Nombre { get; set; } = "";   //PL-ES-F1-015-V01
        public string Handle { get; set; } = "";   //handle del viewport: la identidad real
        public string Dwg { get; set; } = "";      //nombre del dwg de esta vista

        public int EscalaDenominador { get; set; }  //25 para 1:25
        public double CustomScale { get; set; }     //mm de papel por unidad de modelo

        //Ventana del viewport, en metros. El centro queda en el origen local de la vista.
        public double VentanaAnchoM { get; set; }
        public double VentanaAltoM { get; set; }
        public double VentanaCentroX { get; set; }  //coordenada real, solo como trazabilidad
        public double VentanaCentroY { get; set; }

        //Centro del viewport en el papel de AutoCAD, en mm
        public double PapelCentroXMm { get; set; }
        public double PapelCentroYMm { get; set; }

        public List<m_TextoCAD> Textos { get; set; } = new List<m_TextoCAD>();

        //Linea de identidad que se guarda en la vista de Revit para poder reprocesar
        public string Identidad(string archivoOrigen) => $"{archivoOrigen}|{Handle}|{VentanaCentroX:R};{VentanaCentroY:R}|{CustomScale:R}";
    }

    /// <summary>Un texto de AutoCAD, con la altura ya convertida a mm de papel.</summary>
    public sealed class m_TextoCAD
    {
        public string Texto { get; set; } = "";
        public double XM { get; set; }              //relativo al centro de la ventana, en metros
        public double YM { get; set; }
        public double AlturaMm { get; set; }        //altura en papel: altura_modelo * customScale
        public double RotacionGrados { get; set; }
        public string Capa { get; set; } = "";
    }

    /// <summary>Fila de la tabla capa de CAD -> estilo de linea de Revit.</summary>
    public sealed class m_MapeoCapa
    {
        public string Capa { get; set; } = "";
        public int Planos { get; set; }           //cuantas veces aparece en el lote
        public string EstiloSelecc { get; set; } = "";
    }

    /// <summary>
    ///     Reconciliacion de una vista: lo que entro, lo que salio y lo que se descarto y por que.
    ///     Si las cuentas no cuadran se sabe sin abrir el plano.
    /// </summary>
    public sealed class m_ReporteVista
    {
        public string Vista { get; set; } = "";

        //Objetos de geometria leidos del dwg
        public int CurvasLeidas { get; set; }
        //Objetos que produjeron al menos una linea de detalle
        public int CurvasConvertidas { get; set; }
        //Objetos que no produjeron nada
        public int CurvasDescartadas { get; set; }
        //Mallas y solidos: no hay linea de detalle equivalente
        public int ObjetosNoSoportados { get; set; }
        //Lineas de detalle creadas: una curva teselada produce varias, por eso va aparte
        public int LineasCreadas { get; set; }

        public int CurvasCortas { get; set; }      //por debajo de la tolerancia de Revit
        public int CurvasAplanadas { get; set; }   //tenian Z variable: aplanarlas las deforma

        public int TextosLeidos { get; set; }
        public int TextosCreados { get; set; }

        public List<string> CapasSinMapear { get; } = new List<string>();
        public List<string> AlturasSinTipo { get; } = new List<string>();
        public string Error { get; set; } = "";

        //Todo objeto leido tiene que haber acabado en una de las tres cestas, y todo texto creado
        public bool Cuadra => Error == ""
            && CurvasLeidas == CurvasConvertidas + CurvasDescartadas + ObjetosNoSoportados
            && TextosCreados == TextosLeidos;

        public string Resumen()
        {
            if (Error != "") return $"{Vista}: FALLO. {Error}";

            string texto = $"{Vista}: {CurvasConvertidas}/{CurvasLeidas} objetos -> {LineasCreadas} lineas, {TextosCreados}/{TextosLeidos} textos";
            if (CurvasDescartadas > 0) texto += $", {CurvasDescartadas} descartado(s)";
            if (ObjetosNoSoportados > 0) texto += $", {ObjetosNoSoportados} malla(s)/solido(s) sin convertir";
            if (CurvasCortas > 0) texto += $", {CurvasCortas} bajo la tolerancia";
            if (CurvasAplanadas > 0) texto += $", {CurvasAplanadas} deformada(s) al aplanar";
            if (CapasSinMapear.Count > 0) texto += $", capas sin mapear: {string.Join(", ", CapasSinMapear.Distinct())}";
            if (AlturasSinTipo.Count > 0) texto += $", alturas sin tipo de texto: {string.Join(", ", AlturasSinTipo.Distinct())}";
            if (!Cuadra) texto += "  <- NO CUADRA";
            return texto;
        }
    }
}
