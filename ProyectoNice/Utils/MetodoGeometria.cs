using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProyectoNice.Utils
{
    public class MetodoGeometria
    {
        public static List<Solid> ObtenerSolidosdeLista (List<Element> listaElem)
        {
            var listaFinalSolidos = new List<Solid>();
            foreach (var element in listaElem)
            {
                List<Solid> solidosdeUno = ObtenerSolidos(element);
                if (solidosdeUno.Count == 1)
                {
                    listaFinalSolidos.Add(solidosdeUno.FirstOrDefault());
                }
                else
                {
                    foreach (var sol in solidosdeUno)
                    {
                        listaFinalSolidos.Add(sol);
                    }
                }
                
              

            }

            return listaFinalSolidos;
        }

        public static List<Solid> ObtenerSolidos (Element elem)
        {
            var listaSolidos = new List<Solid>();
            Options ops = new Options ();
            GeometryElement ge = elem.get_Geometry(ops);

            foreach (GeometryObject go in ge)
            {
                if (go is Solid)
                {
                    Solid solido = (Solid)go;
                    if (solido.Volume > 0)
                    {
                        listaSolidos.Add(solido);
                    }
                }
            }
            return listaSolidos;
        }

        public static PlanarFace ObtenerCaraPlanarEnPunto(Element elem, XYZ punto)
        {
            //Caras en coordenadas del modelo (incluye geometria de familias)
            var caras = new List<PlanarFace>();
            RecolectarCaras(elem.get_Geometry(new Options()), caras);

            return caras
                .Select(c => new { Cara = c, Proy = c.Project(punto) })
                .Where(x => x.Proy != null)
                .OrderBy(x => x.Proy.Distance)
                .Select(x => x.Cara)
                .FirstOrDefault();
        }

        private static void RecolectarCaras(GeometryElement ge, List<PlanarFace> caras)
        {
            foreach (GeometryObject go in ge)
            {
                if (go is Solid solido && solido.Volume > 0)
                    caras.AddRange(solido.Faces.OfType<PlanarFace>());
                else if (go is GeometryInstance gi)
                    RecolectarCaras(gi.GetInstanceGeometry(), caras);
            }
        }
    }
}
