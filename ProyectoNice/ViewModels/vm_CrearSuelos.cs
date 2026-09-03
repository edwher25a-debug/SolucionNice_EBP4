using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using ProyectoNice.Utils;
using ProyectoNice.Views;
using System.Text;
namespace ProyectoNice.ViewModels
{
    public sealed class vm_CrearSuelos : ObservableObject
    {
        //Datos de entrada
        public Document doc;
        public Selection seleccion;
        public StringBuilder sb = new StringBuilder();

        //Niveles
        public List<Level> ListaNivelesCB {  get; set; }
        public Level NivelSeleccCB { get; set; }

        //Tipos de Suelo
        public List<FloorType> ListaTiposCB { get; set; }
        public FloorType TipoSeleccCB { get; set; }

        //Botones
        public RelayCommand AceptarBT { get; set; }
        public RelayCommand CancelarBT { get; set; }

        //Propiedad:View
        public v_CrearSuelos v_CrearSuelos { get; set; }

        //Constructor
        public vm_CrearSuelos(Document doc,Selection seleccion)
        {
            this.doc = doc;
            this.seleccion =  seleccion;

            ObtenerPreData();

        }

        public void ObtenerPreData()
        {
            //Obtener las listas de niveles y TS
            //niveles

            var listaNiveles = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Levels).WhereElementIsNotElementType().ToElements().Cast<Level>().ToList();
            ListaNivelesCB = listaNiveles.OrderBy(x => x.Name).ToList();
            NivelSeleccCB = ListaNivelesCB.FirstOrDefault();
            //tipos de suelos
            var listaTiposSuelos = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).ToElements().Cast<FloorType>().ToList();
            ListaTiposCB = listaTiposSuelos.OrderBy(x => x.Name).ToList();
            TipoSeleccCB = ListaTiposCB.FirstOrDefault();

            //Accion del Boton
            AceptarBT = new RelayCommand(Aceptar);
            CancelarBT = new RelayCommand(Cancelar);

        }

        public void Cancelar()
        {
            //Cerrar Ventana
            v_CrearSuelos.Close();

        }

        public void Aceptar()
        {

            v_CrearSuelos.Close();

            //Acción
            //01_Recolectar vigas y columnas
            var columnas = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType().ToElements().ToList();
            var vigas = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFraming).WhereElementIsNotElementType().ToElements().ToList();

            //02_Extraer Geometrias
            List<Solid> listaSolVigas = MetodoGeometria.ObtenerSolidosdeLista(vigas);
            List<Solid> listaSolColumnas = MetodoGeometria.ObtenerSolidosdeLista(columnas);

            List<Solid> listaSolitosTotal = listaSolColumnas.Concat(listaSolVigas).ToList();

            //03_Juntar todos los Solidos en uno SuperSolido
            Solid SuperSolido = listaSolitosTotal[0];
            for (int i = 1;i<listaSolitosTotal.Count;i++)
            {
                SuperSolido = BooleanOperationsUtils.ExecuteBooleanOperation(SuperSolido, listaSolitosTotal[i], BooleanOperationsType.Union);
            }

            //04_Extraer Caras
            var listaCaraArriba = new List<PlanarFace>();

            foreach (Face cara in SuperSolido.Faces)
            {
                if (cara is PlanarFace)
                {
                    var caraplanar = (PlanarFace)cara;
                    if (caraplanar.FaceNormal.Z == 1)
                    {
                        listaCaraArriba.Add(caraplanar);
                    }

                }

            }
            PlanarFace CaraArriba = listaCaraArriba.FirstOrDefault();

            //05_Obtener contornos interiores
            List<CurveLoop> curveloops = CaraArriba.GetEdgesAsCurveLoops().ToList();
            List<CurveLoop> curveloopsOrdenado = curveloops.OrderBy(cl => cl.GetExactLength()).ToList();

            curveloopsOrdenado.RemoveAt(curveloopsOrdenado.Count- 1);

            //Transaccion
            using (Transaction transaccion = new Transaction(doc, "Creando Suelos"))
            {
                transaccion.Start();

                Floor.Create(doc, curveloopsOrdenado, TipoSeleccCB.Id, NivelSeleccCB.Id);


                transaccion.Commit();
            }




            //foreach (var CurveL in curveloopsOrdenado)
            //{
            //    sb.AppendLine($"Longitud = {CurveL.GetExactLength()}");
            //}
            ////sb.AppendLine($"
            //TaskDialog.Show("MACRO", sb.ToString());
        }



    }
}