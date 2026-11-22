namespace HotaruCodeViz;

/// <param name="DelayMs">見やすい待ち時間</param>
/// <param name="BreakAt">この文字列を含む行にブレークポイントを置く（⏩ 早送りで準備を飛ばせるように）</param>
public sealed record AlgorithmSample(string Category, string Name, string Description, string Code, string Input, int DelayMs, string? BreakAt = null)
{
    public override string ToString() => $"{Category}：{Name}";
}

/// <summary>📚 図鑑のサンプル。どれも競技プログラミングの形式（標準入力から読む）。</summary>
public static class AlgorithmSamples
{
    public static readonly AlgorithmSample[] All =
    {
        new("ソート", "バブルソート",
            "隣どうしを比べて、大きいほうを右へ送る。1周ごとに一番大きい数が右端に決まる。O(N²)",
            """
            using System;
            using System.Linq;

            class Program
            {
                static void Main()
                {
                    int n = int.Parse(Console.ReadLine());
                    int[] a = Console.ReadLine().Split(' ').Select(int.Parse).ToArray();

                    for (int i = 0; i < n - 1; i++)
                    {
                        // 右端の i 個はもう決まっている
                        for (int j = 0; j < n - 1 - i; j++)
                        {
                            if (a[j] > a[j + 1])
                            {
                                int t = a[j];
                                a[j] = a[j + 1];
                                a[j + 1] = t;
                            }
                        }
                    }
                    Console.WriteLine(string.Join(" ", a));
                }
            }
            """,
            "10\n5 3 9 1 7 2 8 6 4 10", 120),

        new("ソート", "選択ソート",
            "残りの中から一番小さい数を探して、左から順に置いていく。交換の回数が少ない。O(N²)",
            """
            using System;
            using System.Linq;

            class Program
            {
                static void Main()
                {
                    int n = int.Parse(Console.ReadLine());
                    int[] a = Console.ReadLine().Split(' ').Select(int.Parse).ToArray();

                    for (int i = 0; i < n - 1; i++)
                    {
                        int minIndex = i;
                        for (int j = i + 1; j < n; j++)
                        {
                            if (a[j] < a[minIndex]) minIndex = j;
                        }
                        int t = a[i];
                        a[i] = a[minIndex];
                        a[minIndex] = t;
                    }
                    Console.WriteLine(string.Join(" ", a));
                }
            }
            """,
            "10\n5 3 9 1 7 2 8 6 4 10", 150),

        new("ソート", "挿入ソート",
            "左側を整列済みに保ちながら、次の数を入るべき場所まで左へずらして入れる。ほぼ整列済みなら速い。O(N²)",
            """
            using System;
            using System.Linq;

            class Program
            {
                static void Main()
                {
                    int n = int.Parse(Console.ReadLine());
                    int[] a = Console.ReadLine().Split(' ').Select(int.Parse).ToArray();

                    for (int i = 1; i < n; i++)
                    {
                        int key = a[i];
                        int j = i - 1;
                        while (j >= 0 && a[j] > key)
                        {
                            a[j + 1] = a[j];
                            j--;
                        }
                        a[j + 1] = key;
                    }
                    Console.WriteLine(string.Join(" ", a));
                }
            }
            """,
            "10\n5 3 9 1 7 2 8 6 4 10", 150),

        new("ソート", "クイックソート",
            "基準（ピボット）より小さい数を左に集めて、左右をそれぞれ再帰で並べる。平均 O(N log N)",
            """
            using System;
            using System.Linq;

            class Program
            {
                static void QuickSort(int[] a, int lo, int hi)
                {
                    if (lo >= hi) return;
                    int pivot = a[hi];
                    int i = lo;
                    for (int j = lo; j < hi; j++)
                    {
                        if (a[j] < pivot)
                        {
                            int t = a[i];
                            a[i] = a[j];
                            a[j] = t;
                            i++;
                        }
                    }
                    int u = a[i];
                    a[i] = a[hi];
                    a[hi] = u;
                    QuickSort(a, lo, i - 1);
                    QuickSort(a, i + 1, hi);
                }

                static void Main()
                {
                    int n = int.Parse(Console.ReadLine());
                    int[] a = Console.ReadLine().Split(' ').Select(int.Parse).ToArray();
                    QuickSort(a, 0, n - 1);
                    Console.WriteLine(string.Join(" ", a));
                }
            }
            """,
            "12\n5 3 9 1 7 12 2 8 11 6 4 10", 150),

        new("ソート", "マージソート",
            "半分に分けてそれぞれを並べ、2つの整列済みの列を小さいほうから合わせる（マージ）。O(N log N)",
            """
            using System;
            using System.Linq;

            class Program
            {
                // a の [left, right) を並べる
                static void MergeSort(int[] a, int[] tmp, int left, int right)
                {
                    if (right - left <= 1) return;
                    int mid = (left + right) / 2;
                    MergeSort(a, tmp, left, mid);
                    MergeSort(a, tmp, mid, right);

                    int i = left, j = mid, k = left;
                    while (i < mid && j < right)
                    {
                        if (a[i] <= a[j]) tmp[k++] = a[i++];
                        else tmp[k++] = a[j++];
                    }
                    while (i < mid) tmp[k++] = a[i++];
                    while (j < right) tmp[k++] = a[j++];
                    for (int p = left; p < right; p++) a[p] = tmp[p];
                }

                static void Main()
                {
                    int n = int.Parse(Console.ReadLine());
                    int[] a = Console.ReadLine().Split(' ').Select(int.Parse).ToArray();
                    int[] tmp = new int[n];
                    MergeSort(a, tmp, 0, n);
                    Console.WriteLine(string.Join(" ", a));
                }
            }
            """,
            "12\n5 3 9 1 7 12 2 8 11 6 4 10", 150),

        new("探索", "二分探索",
            "整列済みの配列で、真ん中と比べて探す範囲を半分ずつ狭める。O(log N)",
            """
            using System;
            using System.Linq;

            class Program
            {
                static void Main()
                {
                    // 1行目: N と探す数 x、2行目: 小さい順に N 個
                    int[] nx = Console.ReadLine().Split(' ').Select(int.Parse).ToArray();
                    int n = nx[0], x = nx[1];
                    int[] a = Console.ReadLine().Split(' ').Select(int.Parse).ToArray();

                    int lo = 0, hi = n - 1;
                    int found = -1;
                    while (lo <= hi)
                    {
                        int mid = (lo + hi) / 2;
                        if (a[mid] == x)
                        {
                            found = mid;
                            break;
                        }
                        else if (a[mid] < x) lo = mid + 1;
                        else hi = mid - 1;
                    }
                    Console.WriteLine(found >= 0 ? $"{x} は {found} 番目" : $"{x} はありません");
                }
            }
            """,
            "16 37\n2 5 8 12 16 23 29 31 37 42 48 53 61 70 77 85", 400),

        new("数学", "エラトステネスの篩",
            "2 から順に、素数の倍数を消していく。残ったものが素数。O(N log log N)",
            """
            using System;
            using System.Collections.Generic;

            class Program
            {
                static void Main()
                {
                    int n = int.Parse(Console.ReadLine());
                    bool[] isPrime = new bool[n + 1];
                    for (int i = 2; i <= n; i++) isPrime[i] = true;

                    for (int i = 2; i * i <= n; i++)
                    {
                        if (!isPrime[i]) continue;
                        for (int j = i * i; j <= n; j += i)
                        {
                            isPrime[j] = false;
                        }
                    }

                    var primes = new List<int>();
                    for (int i = 2; i <= n; i++)
                    {
                        if (isPrime[i]) primes.Add(i);
                    }
                    Console.WriteLine(string.Join(" ", primes));
                }
            }
            """,
            "60", 80),

        new("数学", "累積和",
            "s[i] = a[0] + … + a[i-1] を先に作っておくと、区間 [l, r) の和が s[r] - s[l] の1回の引き算で出せる。",
            """
            using System;
            using System.Linq;

            class Program
            {
                static void Main()
                {
                    int n = int.Parse(Console.ReadLine());
                    int[] a = Console.ReadLine().Split(' ').Select(int.Parse).ToArray();

                    int[] s = new int[n + 1];
                    for (int i = 0; i < n; i++)
                    {
                        s[i + 1] = s[i] + a[i];
                    }

                    // 3行目: 区間 [l, r)
                    int[] lr = Console.ReadLine().Split(' ').Select(int.Parse).ToArray();
                    int l = lr[0], r = lr[1];
                    Console.WriteLine($"a[{l}] から a[{r - 1}] までの和 = {s[r] - s[l]}");
                }
            }
            """,
            "8\n3 1 4 1 5 9 2 6\n2 6", 250),

        new("DP", "ナップサック問題",
            "dp[i, c] = 最初の i 個から重さ c 以内で選んだときの価値の最大。表を上の行から埋めていく。O(NW)",
            """
            using System;
            using System.Linq;

            class Program
            {
                static void Main()
                {
                    // 1行目: 品物の数 N と容量 W、続く N 行: 重さと価値
                    int[] first = Console.ReadLine().Split(' ').Select(int.Parse).ToArray();
                    int n = first[0], W = first[1];
                    int[] weight = new int[n];
                    int[] value = new int[n];
                    for (int k = 0; k < n; k++)
                    {
                        int[] wv = Console.ReadLine().Split(' ').Select(int.Parse).ToArray();
                        weight[k] = wv[0];
                        value[k] = wv[1];
                    }

                    int[,] dp = new int[n + 1, W + 1];
                    for (int i = 0; i < n; i++)
                    {
                        for (int c = 0; c <= W; c++)
                        {
                            dp[i + 1, c] = dp[i, c]; // 選ばない
                            if (c >= weight[i])
                            {
                                dp[i + 1, c] = Math.Max(dp[i + 1, c], dp[i, c - weight[i]] + value[i]); // 選ぶ
                            }
                        }
                    }
                    Console.WriteLine(dp[n, W]);
                }
            }
            """,
            "4 7\n3 4\n4 5\n2 3\n1 2", 60),

        new("DP", "最長共通部分列（LCS）",
            "dp[i, j] = s の先頭 i 文字と t の先頭 j 文字の LCS の長さ。文字が同じなら左上 + 1、違えば上と左の大きいほう。",
            """
            using System;

            class Program
            {
                static void Main()
                {
                    string s = Console.ReadLine();
                    string t = Console.ReadLine();
                    int[,] dp = new int[s.Length + 1, t.Length + 1];

                    for (int i = 1; i <= s.Length; i++)
                    {
                        for (int j = 1; j <= t.Length; j++)
                        {
                            if (s[i - 1] == t[j - 1])
                                dp[i, j] = dp[i - 1, j - 1] + 1;
                            else
                                dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
                        }
                    }
                    Console.WriteLine(dp[s.Length, t.Length]);
                }
            }
            """,
            "ABCBDAB\nBDCABA", 60),

        new("グラフ", "迷路の幅優先探索（BFS）",
            "スタートから近い順にマスを広げていき、各マスまでの最短の歩数を dist に書く。O(HW)",
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            class Program
            {
                static void Main()
                {
                    int[] hw = Console.ReadLine().Split(' ').Select(int.Parse).ToArray();
                    int h = hw[0], w = hw[1];
                    string[] maze = new string[h];
                    for (int k = 0; k < h; k++) maze[k] = Console.ReadLine();

                    int[,] dist = new int[h, w];
                    int sy = 0, sx = 0, gy = 0, gx = 0;
                    for (int r = 0; r < h; r++)
                    {
                        for (int c = 0; c < w; c++)
                        {
                            dist[r, c] = -1;
                            if (maze[r][c] == 'S') { sy = r; sx = c; }
                            if (maze[r][c] == 'G') { gy = r; gx = c; }
                        }
                    }

                    int[] dy = { 1, -1, 0, 0 };
                    int[] dx = { 0, 0, 1, -1 };
                    var queue = new Queue<(int, int)>();
                    dist[sy, sx] = 0;
                    queue.Enqueue((sy, sx));
                    while (queue.Count > 0)
                    {
                        var (y, x) = queue.Dequeue();
                        for (int d = 0; d < 4; d++)
                        {
                            int ny = y + dy[d], nx = x + dx[d];
                            if (maze[ny][nx] == '#' || dist[ny, nx] != -1) continue;
                            dist[ny, nx] = dist[y, x] + 1;
                            queue.Enqueue((ny, nx));
                        }
                    }
                    Console.WriteLine($"ゴールまで {dist[gy, gx]} 歩");
                }
            }
            """,
            "7 9\n#########\n#S..#...#\n#.#.#.#.#\n#.#...#.#\n#.#####.#\n#......G#\n#########", 40,
            BreakAt: "queue.Enqueue((sy, sx));"),

        new("再帰", "フィボナッチ（素朴な再帰）",
            "Fib(k) = Fib(k-1) + Fib(k-2) をそのまま書くと、同じ計算を何度もくり返す。🌳 呼び出しタブの紫の「×N」が同じ引数で呼ばれた回数。O(2^N)",
            """
            using System;

            class Program
            {
                static int Fib(int k)
                {
                    if (k <= 1) return k;
                    return Fib(k - 1) + Fib(k - 2);
                }

                static void Main()
                {
                    int n = int.Parse(Console.ReadLine());
                    Console.WriteLine($"Fib({n}) = {Fib(n)}");
                }
            }
            """,
            "6", 150),
        new("再帰", "フィボナッチ（メモ化再帰）",
            "一度計算した Fib(k) を memo に覚えておくと、同じ計算をくり返さずに済む。O(N)",
            """
            using System;

            class Program
            {
                static long[] memo;

                static long Fib(int k)
                {
                    if (k <= 1) return k;
                    if (memo[k] != 0) return memo[k];
                    memo[k] = Fib(k - 1) + Fib(k - 2);
                    return memo[k];
                }

                static void Main()
                {
                    int n = int.Parse(Console.ReadLine());
                    memo = new long[n + 1];
                    Console.WriteLine($"Fib({n}) = {Fib(n)}");
                }
            }
            """,
            "20", 150),
    };
}
