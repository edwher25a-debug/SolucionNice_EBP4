namespace ProyectoNice.Models
{
    //Punto en coordenadas compartidas (metros)
    public class m_PuntoCompartido
    {
        public string Nombre { get; set; } = "";
        public double Este { get; set; }
        public double Norte { get; set; }
        public double Elevacion { get; set; }
    }
}
