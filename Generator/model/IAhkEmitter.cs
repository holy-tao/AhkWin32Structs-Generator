
using System.Text;

public interface IAhkEmitter
{
    public void ToAhk(StringBuilder sb);

    public string GetDesiredFilepath(string root);

    public string ToAhk()
    {
        StringBuilder sb = new();
        ToAhk(sb);
        return sb.ToString();
    }
}