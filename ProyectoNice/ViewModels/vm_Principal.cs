using Autodesk.Revit.UI.Selection;
using ProyectoNice.Models;
using ProyectoNice.Views;

namespace ProyectoNice.ViewModels
{
    public sealed class vm_Principal : ObservableObject
    {
        //Datos de entrada
        public Document doc;
        public Selection seleccion;

        //Menu
        public List<m_Herramienta> Herramientas { get; }
        private readonly Dictionary<m_Herramienta, vm_Herramienta> creadas = new Dictionary<m_Herramienta, vm_Herramienta>();

        private m_Herramienta herramientaSelecc;
        public m_Herramienta HerramientaSelecc
        {
            get => herramientaSelecc;
            set
            {
                if (!SetProperty(ref herramientaSelecc, value)) return;
                Contenido = value == null ? null : ObtenerHerramienta(value);
            }
        }

        //Viewmodel de la herramienta activa (la vista sale de los DataTemplate de v_Principal)
        private vm_Herramienta contenido;
        public vm_Herramienta Contenido
        {
            get => contenido;
            private set => SetProperty(ref contenido, value);
        }

        //Barra de estado
        private string estado = "Listo.";
        public string Estado
        {
            get => estado;
            set => SetProperty(ref estado, value);
        }

        //Botones
        public RelayCommand CerrarBT { get; }

        //Propiedad:View
        public v_Principal v_Principal { get; set; }

        //Accion de Revit que se ejecuta con la ventana cerrada
        private Action pendiente;

        //Constructor
        public vm_Principal(Document doc, Selection seleccion)
        {
            this.doc = doc;
            this.seleccion = seleccion;

            Herramientas = new List<m_Herramienta>
            {
                new m_Herramienta { Grupo = "REFUERZO", Nombre = "Aceros pilotes",
                    Descripcion = "Barras longitudinales y flejes circulares a partir de la cara superior del pilote.",
                    Crear = () => new vm_ACEROS_PILOTES(doc, seleccion) },

                new m_Herramienta { Grupo = "MODELADO", Nombre = "Crear suelos",
                    Descripcion = "Suelo en los vanos que dejan las vigas y columnas estructurales.",
                    Crear = () => new vm_CrearSuelos(doc, seleccion) },
                new m_Herramienta { Grupo = "MODELADO", Nombre = "Tuberias desde CAD",
                    Descripcion = "Tuberias a partir de una capa de un CAD vinculado (en desarrollo).",
                    Crear = () => new vm_TuberiasporCAD(doc, seleccion) },
                new m_Herramienta { Grupo = "MODELADO", Nombre = "Insertar entre puntos",
                    Descripcion = "Familias de dos puntos en cadena entre las familias que seleccione.",
                    Crear = () => new vm_InsertFamily(doc, seleccion) },
                new m_Herramienta { Grupo = "MODELADO", Nombre = "Colocar por coordenadas",
                    Descripcion = "Familias en coordenadas compartidas desde la tabla o un CSV.",
                    Crear = () => new vm_CoordCompartidas(doc) },

                new m_Herramienta { Grupo = "DATOS", Nombre = "Coordenadas a parametros",
                    Descripcion = "Escribe las coordenadas compartidas (m) en los parametros X, Y, Z de la familia elegida, solo en la vista activa.",
                    Crear = () => new vm_TrabajoHome(doc, seleccion) },
                new m_Herramienta { Grupo = "DATOS", Nombre = "Codigo BIM",
                    Descripcion = "Llena el parametro CodigoBIM: PROYECTO-CATEGORIA-ID-CONSECUTIVO.",
                    Crear = () => new TrabajoFinal(doc, seleccion) },
                new m_Herramienta { Grupo = "DATOS", Nombre = "Parametros desde presupuesto",
                    Descripcion = "Compara los parametros 02_xx (codigo, capitulo, subcapitulo, nombre, unidad) con el Excel del presupuesto y aplica los cambios que marque.",
                    Crear = () => new vm_ParametrosPresupuesto(doc) },

                new m_Herramienta { Grupo = "DOCUMENTACION", Nombre = "Cota en curva",
                    Descripcion = "Cota dibujada que sigue una linea curva y muestra su longitud medida sobre la curva.",
                    Crear = () => new vm_CotaCurva(doc, seleccion) },
                new m_Herramienta { Grupo = "DOCUMENTACION", Nombre = "Corte desarrollado",
                    Descripcion = "Corte a lo largo de una linea guia curva, desenrollado en una vista de dibujo.",
                    Crear = () => new vm_CorteDesarrollado(doc, seleccion) },
                new m_Herramienta { Grupo = "DOCUMENTACION", Nombre = "Acotar vistas",
                    Descripcion = "Acota plantas, vigas cabezal, columnas, alzados, secciones y boxes segun las reglas del proyecto.",
                    Crear = () => new vm_AcotarVistas(doc, seleccion) },
                new m_Herramienta { Grupo = "DOCUMENTACION", Nombre = "Secciones desde lineas",
                    Descripcion = "Una seccion por cada linea de un estilo: plantilla, cotas de box y colocacion en un plano.",
                    Crear = () => new vm_SeccionesDesdeLineas(doc, seleccion) },

                new m_Herramienta { Grupo = "EXPORTAR", Nombre = "Planos a PDF y DWG",
                    Descripcion = "Exporta los planos marcados a PDF (uno por plano o todos juntos) y a DWG.",
                    Crear = () => new vm_ExportarPlanos(doc) },
                new m_Herramienta { Grupo = "EXPORTAR", Nombre = "Fichas para grafo",
                    Descripcion = "Exporta una ficha markdown por elemento coordinable.",
                    Crear = () => new vm_ExportarFichas(doc) }
            };

            CerrarBT = new RelayCommand(() => v_Principal.Close());
        }

        //Crea el viewmodel la primera vez y lo conserva para no perder los datos al reabrir
        private vm_Herramienta ObtenerHerramienta(m_Herramienta herramienta)
        {
            if (creadas.TryGetValue(herramienta, out vm_Herramienta vm)) return vm;

            try
            {
                vm = herramienta.Crear();
                vm.Principal = this;
                creadas[herramienta] = vm;
                Estado = "Listo.";
                return vm;
            }
            catch (Exception ex)
            {
                Estado = $"{herramienta.Nombre}: no se pudo abrir. {ex.Message}";
                return null;
            }
        }

        public void Ejecutar(Action accion)
        {
            pendiente = accion;
            v_Principal.Close();
        }

        //Lo llama el comando al cerrarse la ventana. True: hay que volver a abrirla
        public bool EjecutarPendiente()
        {
            if (pendiente == null) return false;

            Action accion = pendiente;
            pendiente = null;

            Estado = $"{HerramientaSelecc?.Nombre}: terminado.";
            try
            {
                accion();
            }
            catch (Exception ex)
            {
                Estado = $"{HerramientaSelecc?.Nombre}: {ex.Message}";
            }
            return true;
        }
    }
}
