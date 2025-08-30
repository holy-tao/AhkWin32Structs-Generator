
using System.Text;

public interface IAhkEmitter
{
    public void ToAhk(StringBuilder sb);

    public string GetDesiredFilepath(string root);
}