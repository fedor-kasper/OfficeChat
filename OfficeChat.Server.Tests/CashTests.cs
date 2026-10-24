using System.Net;
using OfficeChat.Models;
using OfficeChat.Services;
using Xunit;

namespace OfficeChat.Server.Tests;

public class CashTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "officechat-cash-" + Guid.NewGuid().ToString("N"));
    private readonly FakeNetwork _network = new();
    private readonly List<CashService> _services = new();

    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private CashService Create(string name, string code = "магазин-1")
    {
        var settings = new AppSettings { DisplayName = name, CashMode = true, CashCode = code };
        var service = new CashService(settings, Path.Combine(_folder, name + ".db"), () => _network.Join(), _network.Context) { AutoSync = false };
        service.Start();
        _services.Add(service);
        return service;
    }

    /// <summary>Несколько раундов обмена «я здесь», пока всё не разойдётся.</summary>
    private void Sync(int rounds = 3)
    {
        for (var i = 0; i < rounds; i++)
        {
            foreach (var service in _services) service.SyncNow();
            _network.Pump();
        }
    }

    [Theory]
    [InlineData("672.34", 67234L)]
    [InlineData("672,34", 67234L)]
    [InlineData("1 234", 123400L)]
    [InlineData("700 р", 70000L)]
    [InlineData("700руб.", 70000L)]
    [InlineData("0,5", 50L)]
    [InlineData("1.234", null)]
    [InlineData("-5", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    public void Money_is_parsed_in_kopecks(string text, long? expected) => Assert.Equal(expected, Money.Parse(text));

    [Fact]
    public void Money_is_formatted_in_russian()
    {
        Assert.Equal("1 234,56 ₽", Money.Format(123456).Replace(' ', ' ').Replace(' ', ' '));
        Assert.Equal("+27,66 ₽", Money.FormatSigned(2766));
        Assert.Equal("−20,00 ₽", Money.FormatSigned(-2000));
    }

    [Fact]
    public void Delivery_return_payout_and_cancel_make_the_balance()
    {
        var cash = Create("Касса");
        var milk = cash.AddSupplier("Молочный завод");

        cash.RecordDelivery(milk, due: 67234, paid: 70000, "");   // излишек +27,66
        cash.RecordReturn(milk, 15000, "просрочка");             // возврат +150
        cash.RecordPayout(milk, 10000, "учли в накладной");      // погашение −100
        var wrong = cash.RecordDelivery(milk, 50000, 60000, "");  // ошибка +100…
        Assert.Equal(2766 + 15000 - 10000 + 10000, milk.Balance);

        cash.Cancel(milk.Entries.Single(e => e.Operation.Id == wrong.Id), "ошиблись суммой"); // …отменили
        Assert.Equal(2766 + 15000 - 10000, milk.Balance);
        Assert.Equal(4, milk.Entries.Count); // отменённая запись остаётся в истории
        Assert.Equal(milk.Balance, cash.Total);
        Assert.False(milk.CanRemove);
    }

    [Fact]
    public void Journal_survives_restart()
    {
        var cash = Create("Касса");
        var bread = cash.AddSupplier("Хлебозавод");
        cash.RecordDelivery(bread, 98010, 100000, "");
        cash.Rename(bread, "Хлебозавод №1");
        cash.Dispose();
        _services.Remove(cash);

        var again = Create("Касса");
        var restored = Assert.Single(again.Suppliers);
        Assert.Equal("Хлебозавод №1", restored.Name);
        Assert.Equal(1990, restored.Balance);
    }

    [Fact]
    public void Computers_with_the_same_code_share_the_journal()
    {
        var first = Create("Касса 1");
        var second = Create("Касса 2");
        var other = Create("Чужая касса", code: "другой код");

        var milk = first.AddSupplier("Молочный завод");
        first.RecordDelivery(milk, 67234, 70000, "");
        Sync();

        var seen = Assert.Single(second.Suppliers);
        Assert.Equal("Молочный завод", seen.Name);
        Assert.Equal(2766, seen.Balance);
        Assert.Empty(other.Suppliers); // другой код — ничего не видно

        // Записи с обоих компьютеров, в том числе отмена чужой записи.
        second.RecordReturn(seen, 5000, "");
        second.Cancel(seen.Entries[0], "не та накладная");
        first.RecordPayout(milk, 1000, "");
        Sync();

        Assert.Equal(5000 - 1000, milk.Balance);
        Assert.Equal(milk.Balance, seen.Balance);
        Assert.All(new[] { first, second }, s => Assert.All(s.Peers, p => Assert.True(p.InSync)));
    }

    [Fact]
    public void Lost_packets_and_late_computers_catch_up()
    {
        var first = Create("Касса 1");
        var milk = first.AddSupplier("Молочный завод");
        for (var i = 0; i < 60; i++) first.RecordDelivery(milk, 10000, 10100, $"накладная {i}");

        // Второй компьютер включили позже, и сеть теряет каждый третий пакет.
        _network.DropEvery = 3;
        var second = Create("Касса 2");
        var third = Create("Касса 3");
        Sync(rounds: 10);

        Assert.All(new[] { second, third }, s => Assert.Equal(6000, Assert.Single(s.Suppliers).Balance));
    }

    // ---- Сеть в памяти ----

    private sealed class FakeNetwork
    {
        private readonly List<FakeTransport> _members = new();
        private readonly Queue<Action> _queue = new();
        private int _counter;

        public int DropEvery { get; set; }
        public SynchronizationContext Context { get; }

        public FakeNetwork() => Context = new QueueContext(_queue);

        public ICashTransport Join()
        {
            var transport = new FakeTransport(this, new IPEndPoint(IPAddress.Loopback, 10000 + _members.Count));
            _members.Add(transport);
            return transport;
        }

        public void Deliver(byte[] data, FakeTransport from, FakeTransport to)
        {
            if (DropEvery > 0 && ++_counter % DropEvery == 0) return;
            to.Raise(data, from.EndPoint);
        }

        public void Broadcast(byte[] data, FakeTransport from)
        {
            foreach (var member in _members.Where(m => m != from && !m.Disposed)) Deliver(data, from, member);
        }

        public void Send(byte[] data, FakeTransport from, IPEndPoint to)
        {
            if (_members.FirstOrDefault(m => m.EndPoint.Equals(to) && !m.Disposed) is { } target) Deliver(data, from, target);
        }

        public void Pump()
        {
            for (var i = 0; _queue.Count > 0 && i < 100_000; i++) _queue.Dequeue()();
        }
    }

    private sealed class QueueContext(Queue<Action> queue) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => queue.Enqueue(() => d(state));
    }

    private sealed class FakeTransport(FakeNetwork network, IPEndPoint endPoint) : ICashTransport
    {
        public IPEndPoint EndPoint { get; } = endPoint;
        public bool Disposed { get; private set; }
        public event Action<byte[], IPEndPoint>? Received;
        public void Start() { }
        public void Broadcast(byte[] data) => network.Broadcast(data, this);
        public void Send(byte[] data, IPEndPoint to) => network.Send(data, this, to);
        public void Raise(byte[] data, IPEndPoint from) => Received?.Invoke(data, from);
        public void Dispose() => Disposed = true;
    }
}
