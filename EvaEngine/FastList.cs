using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EvaEngine;
public class FastList<T> : IEnumerable<T>
{
    internal class ListItem
    {
        public ListItem Next;
        public T item;
    }

    internal ListItem Root = new ListItem();
    internal ListItem Last = null!;

    public T First
    {
        get
        {
            if (Root.Next != null) return Root.Next.item;
            else return default(T)!;
        }
    }
    public class Iterator
    {
        readonly FastList<T> _ilist;

        private ListItem prev;
        private ListItem curr;

        internal Iterator(FastList<T> ll)
        {
            _ilist = ll;
            Reset();
        }

        public bool MoveNext(out T v)
        {
            ListItem ll = curr.Next;

            if (ll == null)
            {
                v = default(T);
                _ilist.Last = curr;
                return false;
            }

            v = ll.item;

            prev = curr;
            curr = ll;

            return true;
        }

        public void Remove()
        {
            if (_ilist.Last.Equals(curr)) _ilist.Last = prev;
            prev.Next = curr.Next;
        }

        public void Insert(T item)
        {
            var i = new ListItem()
            {
                item = item,
                Next = curr
            };
            if (prev == null)
                _ilist.Root.Next = i;
            else
                prev.Next = i;
            //if (curr.Equals(_ilist.last))
            //{
            //    _ilist.last = curr;
            //}
        }

        public void Reset()
        {
            this.prev = null;
            this.curr = _ilist.Root;
        }
    }

    public class FastIterator : IEnumerator<T>
    {
        readonly FastList<T> _ilist;

        private ListItem curr;

        internal FastIterator(FastList<T> ll)
        {
            _ilist = ll;
            Reset();
        }

        public object Current => curr.item;

        T IEnumerator<T>.Current => curr.item;

        public void Dispose()
        {

        }

        public bool MoveNext()
        {
            try
            {
                curr = curr.Next;

                return curr != null;
            }
            catch { return false; }
        }

        public void Reset()
        {
            this.curr = _ilist.Root;
        }
    }

    public void Add(T item)
    {
        ListItem li = new ListItem();
        li.item = item;
        if (Root.Next != null && Last != null)
        {
            while (Last.Next != null) Last = Last.Next;
            Last.Next = li;
        }
        else
            Root.Next = li;

        Last = li;

    }

    public T Pop()
    {
        ListItem el = Root.Next;
        Root.Next = el.Next;
        return el.item;
    }

    public Iterator Iterate()
    {
        return new Iterator(this);
    }

    public bool ZeroLen => Root.Next == null;

    public IEnumerator<T> FastIterate()
    {
        return new FastIterator(this);
    }

    public void Unlink()
    {
        Root.Next = null;
        Last = null;
    }

    public int Count()
    {
        int cnt = 0;

        ListItem li = Root.Next;
        while (li != null)
        {
            cnt++;
            li = li.Next;
        }

        return cnt;
    }

    public bool Any()
    {
        return Root.Next != null;
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        throw new Exception("Sorry .GetEnumerator does not support IEnumerator because of Boxing");
        return FastIterate();
    }

    public IEnumerator<T> GetEnumerator()
    {
        return FastIterate();
    }
}