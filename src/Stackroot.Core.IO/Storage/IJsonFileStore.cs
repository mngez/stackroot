namespace Stackroot.Core.IO.Storage;

public interface IJsonFileStore
{
    T Load<T>(string path, Func<T> fallbackFactory);
    void Save<T>(string path, T value);
}
