using System.Collections.Generic;
using System.Linq;

// i made it genmeric to use it for BLAS etc but I forgot i also made it a singleton. Too bored to change it.
// ...
//probably works as a "unique singleton per generic type"
public class ObjectRegistry<T>
{
    private static ObjectRegistry<T> instance;
    public static ObjectRegistry<T> Instance
    {
        get
        {
            if (instance == null)
            {
                instance = new ObjectRegistry<T>();
            }
            return instance;
        }
    }

    private Dictionary<int, T> registry;
    
    private ObjectRegistry()
    {
        registry = new Dictionary<int, T>();
    }

    public void RegisterObject(int key, T obj)
    {
        registry.TryAdd(key, obj);
    }

    public void UnregisterObject(int key)
    {
        registry.Remove(key);
    }

    public T GetObject(int key)
    {
        return registry.GetValueOrDefault(key);
    }

    public bool HasEntry(int key)
    {
        return registry.ContainsKey(key);
    }

    public int GetSize()
    {
        return registry.Count;
    }

    public T[] GetValues()
    {
        T[] values = new T[registry.Count];
        registry.Values.CopyTo(values, 0);
        return values;
    }
}
