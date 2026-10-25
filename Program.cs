using System;
using System.Diagnostics;
using System.Threading;

class Program
{
    static long[][] A = null!, B = null!, C = null!;
    static int n, m, k, T;

    // Общий счётчик выполненных строк. Доступ только через Interlocked / Volatile.
    static int doneRows = 0;

    // Флаг остановки потока-печати прогресса.
    static volatile bool stopPrinter = false;

    static void Main(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine("Использование: dotnet run -c Release -- n m k T");
            Console.Error.WriteLine("Пример:        dotnet run -c Release -- 1000 1000 1000 8");
            return;
        }

        n = int.Parse(args[0]);
        m = int.Parse(args[1]);
        k = int.Parse(args[2]);
        T = int.Parse(args[3]);

        if (n <= 0 || m <= 0 || k <= 0 || T <= 0)
        {
            Console.Error.WriteLine("Все параметры должны быть положительными");
            return;
        }

        // Генерация матриц (время генерации в замер не входит)
        GenerateMatrices();

        // Прогрев JIT: первый вызов Worker внутри процесса идёт медленно,
        // пока JIT-компилятор не оптимизирует код. Прогреваем на маленькой матрице.
        JitWarmup();

        // Свежий результат именно для замеряемого умножения
        C = AllocateResult();
        doneRows = 0;
        stopPrinter = false;

        Console.WriteLine($"A: {n}x{m}, B: {m}x{k}, threads: {T}");

        var sw = Stopwatch.StartNew();

        var printer = new Thread(PrintProgress) { IsBackground = true };
        printer.Start();

        var workers = new Thread[T];
        for (int tid = 0; tid < T; tid++)
        {
            int localTid = tid; // захват переменной цикла
            workers[tid] = new Thread(() => Worker(localTid));
            workers[tid].Start();
        }

        foreach (var w in workers) w.Join();

        sw.Stop();

        stopPrinter = true;
        printer.Join();

        // По завершении гарантированно 100%
        Console.WriteLine("Прогресс: 100%");

        // Контрольная сумма: заодно не даёт компилятору выбросить вычисления
        long checksum = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < k; j++)
                checksum += C[i][j];

        Console.WriteLine($"Время умножения: {sw.Elapsed.TotalSeconds:F3} с");
        Console.WriteLine($"Контрольная сумма: {checksum}");

        if (n <= 10 && k <= 10) PrintMatrix();
    }

    static long[][] AllocateResult()
    {
        var c = new long[n][];
        for (int i = 0; i < n; i++) c[i] = new long[k];
        return c;
    }

    static void GenerateMatrices()
    {
        var rng = new Random(42);
        A = new long[n][];
        for (int i = 0; i < n; i++)
        {
            A[i] = new long[m];
            for (int j = 0; j < m; j++) A[i][j] = rng.Next(10);
        }
        B = new long[m][];
        for (int i = 0; i < m; i++)
        {
            B[i] = new long[k];
            for (int j = 0; j < k; j++) B[i][j] = rng.Next(10);
        }
    }

    // Однократный маленький вызов Worker для JIT-прогрева.
    // Размеры временно подменяются, затем восстанавливаются.
    static void JitWarmup()
    {
        var oldN = n; var oldM = m; var oldK = k;
        var oldA = A; var oldB = B; var oldC = C;

        n = 100; m = 100; k = 100;
        var rng = new Random(1);
        A = new long[n][];
        for (int i = 0; i < n; i++)
        {
            A[i] = new long[m];
            for (int j = 0; j < m; j++) A[i][j] = rng.Next(10);
        }
        B = new long[m][];
        for (int i = 0; i < m; i++)
        {
            B[i] = new long[k];
            for (int j = 0; j < k; j++) B[i][j] = rng.Next(10);
        }
        C = AllocateResult();

        Worker(0);

        n = oldN; m = oldM; k = oldK;
        A = oldA; B = oldB; C = oldC;
    }

    static void Worker(int tid)
    {
        // Статическое распределение: непрерывные блоки, остаток первым потокам.
        int baseCount = n / T;
        int rem = n % T;
        int start = tid * baseCount + Math.Min(tid, rem);
        int cnt = baseCount + (tid < rem ? 1 : 0);

        for (int ii = 0; ii < cnt; ii++)
        {
            int i = start + ii;
            long[] crow = C[i];

            // Порядок i -> t -> j: внутренний цикл идёт подряд по строке B и строке C
            for (int t = 0; t < m; t++)
            {
                long a = A[i][t];
                if (a == 0) continue;
                long[] brow = B[t];
                for (int j = 0; j < k; j++)
                {
                    crow[j] += a * brow[j];
                }
            }

            // Счётчик прогресса: атомарный инкремент.
            Interlocked.Increment(ref doneRows);
        }
    }

    static void PrintProgress()
    {
        int last = -1;
        while (!stopPrinter)
        {
            int done = Volatile.Read(ref doneRows);
            int percent = n > 0 ? (int)((100.0 * done) / n) : 100;

            // 100% печатает main после join, чтобы избежать гонки и дублей
            if (percent < 100 && percent != last)
            {
                Console.WriteLine($"Прогресс: {percent}%");
                last = percent;
            }

            Thread.Sleep(200);
        }
    }

    static void PrintMatrix()
    {
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < k; j++)
            {
                Console.Write(C[i][j]);
                Console.Write(' ');
            }
            Console.WriteLine();
        }
    }
}