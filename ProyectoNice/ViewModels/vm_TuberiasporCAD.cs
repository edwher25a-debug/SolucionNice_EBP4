using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI.Selection;
using ProyectoNice.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProyectoNice.ViewModels
{
    public sealed class vm_TuberiasporCAD : ObservableObject
    {
        //Datos de Entrada
        public Document doc;
        public Selection seleccion;
        //Tuberia
        public List<Element> LstTiposCB {  get; set; }
        public Element TipoSeleccCB { get; set; }
        public List<Element> LstSistLB { get; set; }
        public Element SistSeleccLB { get; set; }
        public List<Level> LstNvlCB { get; set; }
        public Level NvlSeleccCB { get; set; }

        public double DiametroTX { get; set; }
        //CAD
        public List<ImportInstance> LstCADCB { get; set; }

        public ImportInstance _CADSeleccCB;   ///Variable independiente
        public ImportInstance CADSeleccCB
        {
            get { return _CADSeleccCB; }
            set
            {
                _CADSeleccCB = value;
                OnPropertyChanged();
                //Definir accion de obtencion de capas
                LstCapasCB=ObtenerCapas(_CADSeleccCB).OrderBy(capa => capa.Name).ToList();
                CapaSeleccCB = LstCapasCB.FirstOrDefault();
            }

        }
        public List<Category> _LstCapasCB;
        public List<Category> LstCapasCB
        {
            get { return _LstCapasCB; }
            set
            {
                _LstCapasCB = value;
                OnPropertyChanged();
            }


        }

        public Category _CapaSeleccCB;
        public Category CapaSeleccCB
        {
            get { return _CapaSeleccCB; }
            set
            {
                _CapaSeleccCB = value;
                OnPropertyChanged();
            }

        }
        //Relay Command
        public RelayCommand AceptarBT { get; set; }
        public RelayCommand CancelarCB { get;set; }

        //View
        public v_TuberiasporCAD v_TuberiasporCAD { get; set; }

        //Constructor
        public vm_TuberiasporCAD(Document doc, Selection seleccion)
        {
            this.doc = doc;
            this.seleccion = seleccion;

            ObtenerPreData();

        }
        public void ObtenerPreData()
        {
            //Tipos
            List<Element> ListaTiposTub = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).ToElements().ToList();
            LstTiposCB = ListaTiposTub.OrderBy(t => t.Name).ToList();
            TipoSeleccCB=LstTiposCB.FirstOrDefault();

            //Sistemas
            List<Element> ListaSistemasTub = new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).ToElements().ToList();
            LstSistLB = ListaSistemasTub.OrderBy(t => t.Name).ToList();
            SistSeleccLB = LstSistLB.FirstOrDefault();

            //Niveles
            List<Level> ListaNiveles = new FilteredElementCollector(doc).OfClass(typeof(Level)).ToElements().Cast<Level>().ToList();
            LstNvlCB = ListaNiveles.OrderBy(t => t.Name).ToList();
            NvlSeleccCB = LstNvlCB.FirstOrDefault();

            //Lista de CADs
            List<ImportInstance> ListaCADs = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).ToElements().Cast<ImportInstance>().ToList();
            LstCADCB = ListaCADs.OrderBy(t => t.Name).ToList();
            CADSeleccCB = LstCADCB.FirstOrDefault();

            //Lista de Capas
            LstCapasCB = ObtenerCapas(CADSeleccCB).OrderBy(t => t.Name).ToList();
            CapaSeleccCB = LstCapasCB.FirstOrDefault();


        }
        //Cancelar
        //Aceptar


        public List<Category> ObtenerCapas(ImportInstance cad)
        {
            CategoryNameMap ListaCategorias = cad.Category.SubCategories;

            var ListaCapas = new List<Category>();

            foreach (Category capa in ListaCategorias)
            {
                ListaCapas.Add(capa);

            }
            
            return ListaCapas;

        }






    }
}
