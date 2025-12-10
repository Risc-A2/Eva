using System;
using System.Collections;
using System.Runtime.CompilerServices;

namespace EvaEngine;

public class FastCircularQueue<T> : IEnumerable<T> where T : IComparable<T>
{
    private T[] _array;
    private int _head;
    private int _tail;
    private int _size;
    private int _version;
    private double _growthFactor;
    private readonly IComparer<T> _comparer;

    private const int DefaultCapacity = 4;
    private const double DefaultGrowthFactor = 2.0;

    public FastCircularQueue() : this(DefaultCapacity, DefaultGrowthFactor, null) { }

    public FastCircularQueue(int capacity) : this(capacity, DefaultGrowthFactor, null) { }

    public FastCircularQueue(IComparer<T> comparer) : this(DefaultCapacity, DefaultGrowthFactor, comparer) { }

    public FastCircularQueue(int capacity, double growthFactor) : this(capacity, growthFactor, null) { }

    public FastCircularQueue(int capacity, IComparer<T> comparer) : this(capacity, DefaultGrowthFactor, comparer) { }

    public FastCircularQueue(int capacity, double growthFactor, IComparer<T> comparer)
    {
        if (capacity < 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (growthFactor <= 1.0)
            throw new ArgumentOutOfRangeException(nameof(growthFactor));

        _array = capacity == 0 ? Array.Empty<T>() : new T[capacity];
        _growthFactor = growthFactor;
        _comparer = comparer ?? Comparer<T>.Default;
        _head = 0;
        _tail = 0;
        _size = 0;
    }

    public FastCircularQueue(IEnumerable<T> collection) : this(collection, DefaultGrowthFactor, null) { }

    public FastCircularQueue(IEnumerable<T> collection, IComparer<T> comparer) : this(collection, DefaultGrowthFactor, comparer) { }

    public FastCircularQueue(IEnumerable<T> collection, double growthFactor) : this(collection, growthFactor, null) { }

    public FastCircularQueue(IEnumerable<T> collection, double growthFactor, IComparer<T> comparer)
    {
        if (collection == null)
            throw new ArgumentNullException(nameof(collection));
        if (growthFactor <= 1.0)
            throw new ArgumentOutOfRangeException(nameof(growthFactor));

        _growthFactor = growthFactor;
        _comparer = comparer ?? Comparer<T>.Default;

        if (collection is ICollection<T> c)
        {
            int count = c.Count;
            _array = count == 0 ? Array.Empty<T>() : new T[count];
            c.CopyTo(_array, 0);
            _size = count;
            _tail = count;
        }
        else
        {
            _array = new T[DefaultCapacity];
            EnqueueRange(collection);
        }
    }

    public int Count => _size;
    public int Capacity => _array.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Enqueue(T item)
    {
        if (_size == _array.Length)
        {
            GrowExponential(_size + 1);
        }

        _array[_tail] = item;
        MoveTail();
        _size++;
        _version++;
    }

    public void EnqueueRange(IEnumerable<T> items)
    {
        if (items == null)
            throw new ArgumentNullException(nameof(items));

        if (items is ICollection<T> collection)
        {
            // ✅ SOLO 1 COMPARACIÓN DE TAMAÑO para ICollection<T>
            int newItemsCount = collection.Count;
            if (newItemsCount == 0) return;

            int requiredCapacity = _size + newItemsCount;

            // Solo redimensionar una vez si es necesario
            if (requiredCapacity > _array.Length)
            {
                GrowExponential(requiredCapacity);
            }

            // Copiar eficientemente los nuevos elementos
            if (_head < _tail)
            {
                // Caso: [###HEAD---TAIL###]
                int freeSpaceAtEnd = _array.Length - _tail;
                if (newItemsCount <= freeSpaceAtEnd)
                {
                    collection.CopyTo(_array, _tail);
                }
                else
                {
                    int firstSegment = freeSpaceAtEnd;
                    int secondSegment = newItemsCount - firstSegment;

                    // Usar Buffer.BlockCopy para tipos primitivos sería más rápido,
                    // pero CopyTo es genérico y seguro
                    var tempArray = new T[newItemsCount];
                    collection.CopyTo(tempArray, 0);

                    Array.Copy(tempArray, 0, _array, _tail, firstSegment);
                    Array.Copy(tempArray, firstSegment, _array, 0, secondSegment);
                }
            }
            else
            {
                // Caso: [---TAIL###HEAD---] - siempre hay espacio contiguo
                collection.CopyTo(_array, _tail);
            }

            _tail = (_tail + newItemsCount) % _array.Length;
            _size += newItemsCount;
            _version++;
        }
        else
        {
            // Para IEnumerable no-ICollection, usar crecimiento exponencial progresivo
            foreach (var item in items)
            {
                Enqueue(item);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Dequeue()
    {
        if (_size == 0)
            throw new InvalidOperationException("La cola está vacía");

        var item = _array[_head];
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            _array[_head] = default(T);
        }
        MoveHead();
        _size--;
        _version++;
        return item;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Peek()
    {
        if (_size == 0)
            throw new InvalidOperationException("La cola está vacía");

        return _array[_head];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out T result)
    {
        if (_size == 0)
        {
            result = default(T);
            return false;
        }

        result = Dequeue();
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPeek(out T result)
    {
        if (_size == 0)
        {
            result = default(T);
            return false;
        }

        result = Peek();
        return true;
    }

    public void Clear()
    {
        if (_size > 0 && RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            if (_head < _tail)
            {
                Array.Clear(_array, _head, _size);
            }
            else
            {
                Array.Clear(_array, _head, _array.Length - _head);
                Array.Clear(_array, 0, _tail);
            }
        }

        _head = 0;
        _tail = 0;
        _size = 0;
        _version++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MoveTail() => _tail = (_tail + 1) % _array.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MoveHead() => _head = (_head + 1) % _array.Length;

    private void GrowExponential(int requiredCapacity)
    {
        var newCapacity = _array.Length == 0 ? DefaultCapacity : (int)(_array.Length * _growthFactor);

        // Asegurar crecimiento mínimo exponencial
        newCapacity = Math.Max(newCapacity, requiredCapacity);

        // Redondear a la siguiente potencia de 2 para mejor alineación de memoria
        newCapacity = RoundToNextPowerOfTwo(newCapacity);

        // Limitar a Array.MaxLength
        if ((uint)newCapacity > Array.MaxLength)
            newCapacity = Math.Max(requiredCapacity, Array.MaxLength);

        var newArray = new T[newCapacity];

        if (_size > 0)
        {
            if (_head < _tail)
            {
                // [###HEAD---TAIL###] - copia contigua
                Array.Copy(_array, _head, newArray, 0, _size);
            }
            else
            {
                // [---TAIL###HEAD---] - copia en dos segmentos
                int firstSegment = _array.Length - _head;
                Array.Copy(_array, _head, newArray, 0, firstSegment);
                Array.Copy(_array, 0, newArray, firstSegment, _tail);
            }
        }

        _array = newArray;
        _head = 0;
        _tail = _size;
        _version++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RoundToNextPowerOfTwo(int value)
    {
        // Para crecimiento exponencial óptimo en memoria
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        value++;
        return value;
    }

    // Para permitir ajuste dinámico del factor de crecimiento
    public double GrowthFactor
    {
        get => _growthFactor;
        set
        {
            if (value <= 1.0)
                throw new ArgumentOutOfRangeException(nameof(value), "El factor de crecimiento debe ser mayor a 1.0");
            _growthFactor = value;
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        int version = _version;

        for (int i = 0; i < _size; i++)
        {
            if (version != _version)
                throw new InvalidOperationException("La colección fue modificada");

            yield return _array[(_head + i) % _array.Length];
        }
    }
    #region Sorting Methods

    /// <summary>
    /// Ordena los elementos de la cola usando el comparador por defecto
    /// </summary>
    public void Sort()
    {
        Sort(_comparer);
    }

    /// <summary>
    /// Ordena los elementos de la cola usando un comparador específico
    /// </summary>
    public void Sort(IComparer<T> comparer)
    {
        if (_size <= 1) return;
        if (comparer == null) throw new ArgumentNullException(nameof(comparer));

        // Convertir a array lineal para ordenar
        var linearArray = ToLinearArray();

        // Usar Array.Sort que es altamente optimizado
        Array.Sort(linearArray, comparer);

        // Reconstruir la cola ordenada
        RebuildFromArray(linearArray);
        _version++;
    }
    /// <summary>
    /// Ordena los elementos de la cola usando un comparador específico
    /// </summary>
    public void SortTim()
    {
        if (_size <= 1) return;

        // Convertir a array lineal para ordenar
        var linearArray = ToLinearArray();

        // Usar Array.Sort que es altamente optimizado
        Redzen.Sorting.TimSort.Sort<T>(linearArray);
        //Array.Sort(linearArray, comparer);

        // Reconstruir la cola ordenada
        RebuildFromArray(linearArray);
        _version++;
    }

    /// <summary>
    /// Ordena los elementos de la cola usando una función de comparación personalizada
    /// </summary>
    public void Sort(Comparison<T> comparison)
    {
        if (comparison == null) throw new ArgumentNullException(nameof(comparison));
        Sort(Comparer<T>.Create(comparison));
    }

    /// <summary>
    /// Ordena un rango específico de elementos en la cola
    /// </summary>
    public void Sort(int index, int count, IComparer<T> comparer)
    {
        if (index < 0 || index >= _size)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (count < 0 || index + count > _size)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (comparer == null) throw new ArgumentNullException(nameof(comparer));
        if (count <= 1) return;

        var linearArray = ToLinearArray();

        // Ordenar solo el rango especificado
        Array.Sort(linearArray, index, count, comparer);

        RebuildFromArray(linearArray);
        _version++;
    }

    /// <summary>
    /// Ordena la cola en orden descendente
    /// </summary>
    public void SortDescending()
    {
        Sort(Comparer<T>.Create((x, y) => _comparer.Compare(y, x)));
    }

    /// <summary>
    /// Ordena la cola usando un algoritmo de ordenamiento in-place (sin crear arrays adicionales)
    /// </summary>
    public void SortInPlace()
    {
        if (_size <= 1) return;

        // Para colas pequeñas, usar insertion sort in-place
        if (_size <= 16)
        {
            InsertionSortInPlace();
            return;
        }

        // Para colas grandes, convertir a lineal y usar Array.Sort
        var linearArray = ToLinearArray();
        Array.Sort(linearArray, _comparer);
        RebuildFromArray(linearArray);
        _version++;
    }

    /// <summary>
    /// Ordenamiento in-place usando Insertion Sort (eficiente para colas pequeñas)
    /// </summary>
    private void InsertionSortInPlace()
    {
        for (int i = 1; i < _size; i++)
        {
            var current = GetElementAt(i);
            int j = i - 1;

            while (j >= 0 && _comparer.Compare(GetElementAt(j), current) > 0)
            {
                SetElementAt(j + 1, GetElementAt(j));
                j--;
            }
            SetElementAt(j + 1, current);
        }
        _version++;
    }

    /// <summary>
    /// Convierte la cola circular a un array lineal
    /// </summary>
    private T[] ToLinearArray()
    {
        var result = new T[_size];
        if (_size == 0) return result;

        if (_head < _tail)
        {
            Array.Copy(_array, _head, result, 0, _size);
        }
        else
        {
            int firstSegment = _array.Length - _head;
            Array.Copy(_array, _head, result, 0, firstSegment);
            Array.Copy(_array, 0, result, firstSegment, _tail);
        }
        return result;
    }

    /// <summary>
    /// Reconstruye la cola desde un array lineal
    /// </summary>
    private void RebuildFromArray(T[] linearArray)
    {
        Clear();
        _array = new T[Math.Max(linearArray.Length, DefaultCapacity)];
        Array.Copy(linearArray, _array, linearArray.Length);
        _size = linearArray.Length;
        _head = 0;
        _tail = _size;
    }

    /// <summary>
    /// Obtiene el elemento en una posición específica (sin modificar la cola)
    /// </summary>
    private T GetElementAt(int index)
    {
        if (index < 0 || index >= _size)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _array[(_head + index) % _array.Length];
    }

    /// <summary>
    /// Establece el elemento en una posición específica
    /// </summary>
    private void SetElementAt(int index, T value)
    {
        if (index < 0 || index >= _size)
            throw new ArgumentOutOfRangeException(nameof(index));

        _array[(_head + index) % _array.Length] = value;
    }

    #endregion
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
