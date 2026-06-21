namespace Stackroot.Core.IO.Storage;

public interface IJsonFileStore
{
    T? Read<T>(string path);

    T Load<T>(string path, Func<T> fallbackFactory);

    void Save<T>(string path, T value);

    void WriteAtomic<T>(string path, T value);
}
