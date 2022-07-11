using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;

using TShockAPI;
using Terraria;
using TerrariaApi.Server;
using Newtonsoft.Json;
using System.Data;
using TShockAPI.DB;
using MySql.Data.MySqlClient;
using Mono.Data.Sqlite;

using SurvivalCore;
using Microsoft.Xna.Framework;
using Terraria.GameContent.Tile_Entities;
using Terraria.DataStructures;
using TShockAPI.Localization;

namespace SurvivalShop
{
    [ApiVersion(2, 1)]
    public class ShopPlugin : TerrariaPlugin
    {
        public static class ItemDatabase
        {
            internal static IDbConnection DB;
            public static int _lastId;

            public static void Initialize()
            {
                if (DB != null)
                    return;

                IQueryBuilder builder = null;
                switch (TShock.Config.Settings.StorageType)
                {
                    default:
                        return;

                    case "mysql":
                        var hostport = TShock.Config.Settings.MySqlHost.Split(':');
                        DB = new MySqlConnection();
                        DB.ConnectionString = string.Format("Server={0}; Port={1}; Database={2}; Uid={3}; Pwd={4};",
                            hostport[0],
                            hostport.Length > 1 ? hostport[1] : "3306",
                            TShock.Config.Settings.MySqlDbName,
                            TShock.Config.Settings.MySqlUsername,
                            TShock.Config.Settings.MySqlPassword);
                        builder = new MysqlQueryCreator();
                        break;
                    case "sqlite":
                        DB = new SqliteConnection(string.Format("uri=file://tshock//{0},Version=3", "Gangs.sqlite"));
                        builder = new SqliteQueryCreator();
                        break;
                }
                new SqlTableCreator(DB, builder).EnsureTableStructure(new("SurvShop_Items",
                    new("ID", MySqlDbType.Int32) { Primary = true, AutoIncrement = true },
                    new("Data", MySqlDbType.LongText)));

                _lastId = -1;
                using (var reader = DB.QueryReader("SELECT * FROM SurvShop_Items"))
                {
                    while (reader.Read())
                    {
                        ShopItem item = JsonConvert.DeserializeObject<ShopItem>(reader.Reader.GetString(1));
                        item.ID = reader.Reader.GetInt32(0);
                        _lastId = item.ID;
                        Shop.InsertItem(item);
                    }
                }
            }

            public static void AddItem(ShopItem item)
            {
                if (_lastId == -1)
                {
                    using (var query = DB.QueryReader("INSERT INTO SurvShop_Items VALUES(@0, @1); SELECT LAST_INSERT_ID();", null, JsonConvert.SerializeObject(item)))
                        if (query.Read())
                            _lastId = item.ID = query.Reader.GetInt32(0);
                }
                else
                {
                    DB.Query("INSERT INTO SurvShop_Items VALUES(@0, @1);", null, JsonConvert.SerializeObject(item));
                    item.ID = ++_lastId;
                }
                
                Shop.InsertItem(item);
            }
        }
        public static class PriceDatabase
        {
            internal static IDbConnection DB;

            public static void Initialize()
            {
                if (DB != null)
                    return;

                IQueryBuilder builder = null;
                switch (TShock.Config.Settings.StorageType)
                {
                    default:
                        return;

                    case "mysql":
                        var hostport = TShock.Config.Settings.MySqlHost.Split(':');
                        DB = new MySqlConnection();
                        DB.ConnectionString = string.Format("Server={0}; Port={1}; Database={2}; Uid={3}; Pwd={4};",
                            hostport[0],
                            hostport.Length > 1 ? hostport[1] : "3306",
                            TShock.Config.Settings.MySqlDbName,
                            TShock.Config.Settings.MySqlUsername,
                            TShock.Config.Settings.MySqlPassword);
                        builder = new MysqlQueryCreator();
                        break;
                    case "sqlite":
                        DB = new SqliteConnection(string.Format("uri=file://tshock//{0},Version=3", "Gangs.sqlite"));
                        builder = new SqliteQueryCreator();
                        break;
                }
                new SqlTableCreator(DB, builder).EnsureTableStructure(new("SurvShop_Price",
                    new("ID", MySqlDbType.Int32) { Primary = true, AutoIncrement = true },
                    new("Price", MySqlDbType.Int64)));
            }

            public static long GetPriceForUser(int id)
            {
                using (var query = DB.QueryReader("SELECT Price FROM SurvShop_Price WHERE ID=@0", id))
                {
                    var reader = query.Reader;
                    if (query.Read())
                    {
                        return reader.GetInt64(0);
                    }
                }
                return -1;
            }
            public static void UpdatePriceForUser(int id, long price, bool needCheck = true)
            {
                if (needCheck && GetPriceForUser(id) == -1)
                    DB.Query("INSERT INTO SurvShop_Price VALUES(@0, @1)", null, price);
                else
                    DB.Query("UPDATE SurvShop_Price SET Price=@0 WHERE ID=@1", price, id);
            }
        }

        public override string Author => "Zoom L1";
        public override string Name => "Survival Shop";
        public ShopPlugin(Main game) : base(game) { }

        public Command[] _Commands;

        public override void Initialize()
        {
            Wipe.Events.PreWipe += OnPreWipe;
            PriceDatabase.Initialize();

            ServerApi.Hooks.GamePostInitialize.Register(this, PostInitialize, -1);
            ServerApi.Hooks.NetGetData.Register(this, OnGetData);

            _Commands = new Command[] {
            
                new Command(SPermissions.ShopManipule, SellItemCommand, "sell"),
                new Command(SPermissions.ShopManipule, GetPriceCommand, "price"),
                new Command(SPermissions.Admin, InsertItem, "shop_insertitem")
            };
            Commands.ChatCommands.AddRange(_Commands);
        }

        private void OnGetData(GetDataEventArgs args)
        {
            if (args.Handled)
                return;
            if (args.MsgID == PacketTypes.SignRead)
            {
                using (BinaryReader reader = new BinaryReader(new MemoryStream(args.Msg.readBuffer, args.Index, args.Length)))
                {
                    int x = reader.ReadInt16();
                    int y = reader.ReadInt16();

                    var tuple = Shop.Frames.Find(p => p.Item1.X == x && p.Item1.Y == y);
                    if (tuple == null)
                        return;
                    args.Handled = true;
                    Shop.TryBuy(tuple, TShock.Players[args.Msg.whoAmI]);
                }
                    
            }
        }
        private void PostInitialize(EventArgs e)
        {
            Shop.Region = TShock.Regions.GetRegionByName("ServerShop");

            if (Shop.Region == null)
                return;

            Shop.Frames = new List<Tuple<Point, Point>>();

            GetFrames(Shop.Region);
            Shop.Items = new List<ShopItem>();
            Shop.Queue = new Queue<ShopItem>();

            ItemDatabase.Initialize();

            if (Shop.Items.Count < Shop.Frames.Count)
                for (int i = Shop.Items.Count; i < Shop.Frames.Count; i++)
                {
                    var item = Shop.Frames[i];
                    Shop.FrameItem(item.Item2, null, false);
                    Shop.SignText(item.Item1, null, false);
                }
        }
        private void OnPreWipe()
        {
            ItemDatabase.DB.Query("DELETE FROM SurvShop_Items");
            PriceDatabase.DB.Query("DELETE FROM SurvShop_Price");
        }

        private void SellItemCommand(CommandArgs args)
        {
            if (Shop.Region == null)
            {
                args.Player.SendErrorMessage("???");
                return;
            }

            if (args.Player.SelectedItem == null || args.Player.SelectedItem.stack == 0 || args.Player.TPlayer.inventory[args.Player.TPlayer.selectedItem] == null)
            {
                args.Player.SendErrorMessage("Возьмите в руку предмет, который Вы хотите продать.");
                return;
            }

            if (args.Parameters.Count == 0 || args.Parameters.Count > 1)
            {
                args.Player.SendErrorMessage("Пожалуйста, введите сумму.");
                return;
            }

            if (!TryParseCoins(args.Parameters[0], out (int, int, int, int) raw))
            {
                args.Player.SendErrorMessage("Вы должны ввести сумму. Пример: 1p2g3s4c = 1 платина, 2 золотых, 3 серебра и 4 медных монеты");
                return;
            }

            int price = Item.buyPrice(raw.Item1, raw.Item2, raw.Item3, raw.Item4);
            if (price <= 0)
            {
                args.Player.SendInfoMessage("Вы должны ввести позитивно-положительную сумму.");
                return;
            }

            var item = args.Player.SelectedItem;
            if (item.IsACoin)
            {
                args.Player.SendErrorMessage("Вы не можете продавать монеты.");
                return;
            }


            if (!args.Player.HasPermission("shop.userplus"))
            {
                if (Shop.Items.Count(i => i.Owner == args.Player.Account.ID) > 6)
                {
                    args.Player.SendErrorMessage("Вы можете продать только шесть вещей, пожалуйста подождите, пока их кто-то купит\n(либо уберите свои старые)");
                    return;
                }
                if (Shop.Queue.Count > 15)
                {
                    args.Player.SendErrorMessage("На складе магазина может храниться только 15 предметов!");
                    return;
                }
            }

            var shopItem = new ShopItem() { Item = new NetItem(item.netID, item.stack, item.prefix), Owner = args.Player.Account.ID, Price = price };

            args.Player.TPlayer.HeldItem.stack = 0;
            args.Player.SendData(PacketTypes.PlayerSlot, "", args.Player.Index, args.Player.TPlayer.selectedItem);

            ItemDatabase.AddItem(shopItem);

            Shop.Send(args.Player, 4);
            //args.Player.SendInfoMessage("Вы продали предмет.");
        }
        private void GetPriceCommand(CommandArgs args)
        {
            if (Shop.Region == null)
            {
                args.Player.SendErrorMessage("???");
                return;
            }
            var price = PriceDatabase.GetPriceForUser(args.Player.Account.ID);
            if (price < 0)
            {
                args.Player.SendInfoMessage("Вам ничего забирать.");
                return;
            }
            var raw = SurvivalCorePlugin.buyPrice((int)price);

            if (args.Parameters.Count == 0)
            {
                args.Player.SendInfoMessage("У вас сейчас: ", Shop.ToPlayerPrice(raw));
            }
            else
            {
                int newPrice = Item.buyPrice(raw.Item1, raw.Item2, raw.Item3, raw.Item4);

                PriceDatabase.UpdatePriceForUser(args.Player.Account.ID, price-newPrice, false);

                if (raw.Item1 > 0)
                    args.Player.GiveItem(74, raw.Item1);
                if (raw.Item2 > 0)
                    args.Player.GiveItem(73, raw.Item2);
                if (raw.Item3 > 0)
                    args.Player.GiveItem(72, raw.Item3);
                if (raw.Item4 > 0)
                    args.Player.GiveItem(71, raw.Item4);

                args.Player.SendErrorMessage("Вы забрали накопления из банка.");
            }
        }
        private void InsertItem(CommandArgs args)
        {
            Shop.InsertItem(new ShopItem() { Owner = args.Player.Account.ID, Price = 1, Item = new NetItem(int.Parse(args.Parameters[0]), int.Parse(args.Parameters[1]), byte.Parse(args.Parameters[2])) });
        }

        internal static void GetFrames(Region region)
        {
            var rec = new Rectangle(region.Area.X, region.Area.Y, region.Area.X + region.Area.Width, region.Area.Y + region.Area.Height);

            List<Point> signs = new List<Point>();

            for (int y = rec.Y; y <= rec.Height; y++)
                for (int x = rec.X; x <= rec.Width; x++)
                {
                    var tile = Main.tile[x, y];
                    if (tile.active() && tile.type == Terraria.ID.TileID.Signs)
                    {
                        var signIndex = Sign.ReadSign(x, y, true);
                        if (signIndex == -1)
                            continue;
                        var point = new Point(Main.sign[signIndex].x, Main.sign[signIndex].y);
                        if (!signs.Contains(point))
                            signs.Add(point);
                    }
                }

            foreach (var point in signs)
            {
                if (Main.tile[point.X, point.Y-2].type == Terraria.ID.TileID.ItemFrame)
                    Shop.Frames.Add(new Tuple<Point, Point>(point, new Point(point.X, point.Y-2)));
            }
        }
        public static bool TryParseCoins(string str, out (int, int, int, int) raw)
        {
            raw = (0, 0, 0, 0);
            int p = 0;
            int g = 0;
            int s = 0;
            int c = 0;

            var sb = new StringBuilder(3);
            for (int i = 0; i < str.Length; i++)
            {
                if (Char.IsDigit(str[i]) || (str[i] == '-' || str[i] == '+' || str[i] == ' '))
                    sb.Append(str[i]);
                else
                {
                    if (!uint.TryParse(sb.ToString(), out uint num))
                        return false;

                    sb.Clear();
                    switch (str[i])
                    {
                        case 'p':
                            p = (int)num;
                            break;
                        case 'g':
                            g = (int)num;
                            break;
                        case 's':
                            s = (int)num;
                            break;
                        case 'c':
                            c = (int)num;
                            break;

                        default:
                            return false;
                    }
                }
            }
            raw = (p, g, s, c);
            if (sb.Length != 0)
                return false;
            return true;
        }
    }

    public static class Shop
    {
        public static Region Region;
        public static List<Tuple<Point, Point>> Frames;

        public static List<ShopItem> Items;
        internal static Queue<ShopItem> Queue;

        #region UserInterface
        static string[] Welcome = new string[]
        {
            "Эй! Иди сюда, подкину тебе кое-чего!",
            "Ну! Чё стоишь? Подходи, не кусаюсь",
            "Здорово, рад тебя видеть! Ну чё, давай о деле поговорим?",
            "Лампы, веревки, бомбы, тебе всё это нужно? Оно твоё, мой друг, если у тебя достаточно рупий",
            "Эй {0}, у меня для тебя хорошее предложение<300>Посмотришь?",
            "Эй! Мужик, пойдем в подвал поговорим?"
        };

        static string[] LackMoney = new string[]
        {
            "Моры не хватило...",
            "Опять работать?",
            "Нужно больше золота"
        };

        static string[] Purchase = new string[]
        {
            "Отличный выбор!",
            "Продано!",
            "Поздравляем с покупкой!",
            "Заходи ещё!"
        };
        static string[] ReturnPurchase = new string[]
        {
            "Моя прелесть...",
            "Это принадлежит мне!",
            "И как это сюда попало?"
        };
        static string[] AdminPurchase = new string[]
        {
            "Лот очищен!",
            "Товар конфискован!",
            "Товар удален с рынка."
        };

        static string[] Sell = new string[]
        {
            "Торги начались!",
            "Ждём покупателя...",
            "...Ожидание покупки...",
            "Рынок пополнен!"
        };

        static string[] SecretPhrases = new string[]
        {
            "Священные лисицы благословили тебя"
        };
        static Random random = new Random();
        static string GetText(int i)
        {
            switch (i)
            {
                default:
                    return null;

                case 0:
                    return LackMoney[random.Next(0, LackMoney.Length)];
                case 1:
                    return Purchase[random.Next(0, Purchase.Length)];
                case 2:
                    return ReturnPurchase[random.Next(0, ReturnPurchase.Length)];
                case 3:
                    return AdminPurchase[random.Next(0, AdminPurchase.Length)];
                case 4:
                    return Sell[random.Next(0, Sell.Length)];
            }
        }

        static Color[] _Colors = new Color[]
        {
            new Color(255, 102, 102),
            new Color(255, 140, 102),
            new Color(255, 153, 102),
            new Color(255, 179, 102),
            new Color(255, 217, 102),
            new Color(255, 255, 102),
            new Color(217, 255, 102),
            new Color(179, 255, 102),
            new Color(140, 255, 102),
            new Color(102, 255, 102),
            new Color(102, 255, 140),
            new Color(102, 255, 179),
            new Color(102, 255, 217),
            new Color(102, 255, 255),
            new Color(102, 217, 255),
            new Color(102, 179, 255),
            new Color(102, 140, 255),
            new Color(102, 102, 255),
            new Color(140, 102, 255),
            new Color(179, 102, 255),
            new Color(217, 102, 255),
            new Color(255, 102, 255),
            new Color(255, 102, 217),
            new Color(255, 102, 179),
            new Color(255, 102, 140)
        };

        internal static void Send(TSPlayer player, int i)
        {
            player.SendData(PacketTypes.CreateCombatTextExtended, GetText(i), (int)_Colors[random.Next(0, _Colors.Length)].PackedValue, player.X, player.Y);
            //CombatText.NewText(player.TPlayer.position, _Colors[random.Next(0, _Colors.Length)], 1, true);
        }
        #endregion

        public static void TryBuy(Tuple<Point, Point> tuple, TSPlayer player)
        {
            var index = Frames.IndexOf(tuple);
            if (index == -1)
                return;
            if (Items.Count == 0 || (index != 0 && Items.Count < index))
                return;
            var item = Items[index];
            RemoveItem(item);
            if (!player.HasPermission(SPermissions.Admin))
            {
                if (item.Owner != player.Account.ID)
                {
                    if (!SurvivalCorePlugin.TryBuy(player, item.Price))
                    {
                        Send(player, 0);
                        return;
                    }

                    Send(player, 1);

                    var price = ShopPlugin.PriceDatabase.GetPriceForUser(item.Owner);
                    if (price == -1)
                        ShopPlugin.PriceDatabase.UpdatePriceForUser(item.Owner, item.Price, true);
                    else
                        ShopPlugin.PriceDatabase.UpdatePriceForUser(item.Owner, item.Price + price, false);
                }
                else
                {
                    Send(player, 2);
                }
            }
            else
            {
                if (!player.ContainsData("ShopData"))
                {
                    player.SendInfoMessage("Т.к. вы администратор, при покупке предмета в магазине вы просто удаляете его.");
                    player.SetData("ShopData", 1);
                }
                Send(player, 3);
            }
            player.GiveItem(item.Item.NetId, item.Item.Stack, item.Item.PrefixId);
        }

        public static void InsertItem(ShopItem item)
        {
            if (Items.Count < Frames.Count)
                PutItemInIndex(item, Items.Count, TShock.Utils.GetActivePlayerCount() > 0);
            else
                Queue.Enqueue(item);
        }
        public static void RemoveItem(ShopItem item)
        {
            int index = Items.IndexOf(item);
            if (index == -1)
                return;

            for (int i = index; i < Frames.Count; i++)
            {
                ShopItem j = null;
                if (Items.Count > i + 1)
                    j = Items[i + 1];
                PutItemInIndex(j, i, false, false);
            }

            if (Items[index].ID != -1)
                ShopPlugin.ItemDatabase.DB.Query("DELETE FROM SurvShop_Items WHERE ID = @0", Items[index].ID);
            Items.RemoveAt(index);
            Update();
            checkAvailableFrames();
        }
        static void checkAvailableFrames()
        {
            if (Items.Count < Frames.Count && Queue.Count > 0)
            {
                var item = Queue.Dequeue();
                InsertItem(item);
            }
        }
        static void PutItemInIndex(ShopItem item, int index, bool sendData = true, bool needPlace = true)
        {
            bool place = item != null;
            var frame = Frames[index];

            SignText(frame.Item1, item, place, sendData);
            FrameItem(frame.Item2, item, place, sendData);

            if (needPlace && place)
                Items.Add(item);
        }

        internal static void SignText(Point point, ShopItem item, bool place = true, bool needSendData = true)
        {
            int i = Sign.ReadSign((int)point.X, (int)point.Y);
            if (place)
                Sign.TextSign(i, string.Format("{0}{1}{2}\nСтоимость {3}\nПродавец {4}.\n______________________\nНажмите на табличку чтобы купить предмет.",
                    EnglishLanguage.GetItemNameById(item.Item.NetId), item.Item.PrefixId > 0 ? " (" + EnglishLanguage.GetPrefixById(item.Item.PrefixId) + ")" : "", item.Item.Stack > 1 ? " (" + item.Item.Stack + ")" : "",
                    ToPlayerPrice(SurvivalCorePlugin.buyPrice((int)item.Price)),
                    TShock.UserAccounts.GetUserAccountByID(item.Owner).Name));
            else
                Sign.TextSign(i, "Не продается.");
            if (needSendData)
                TSPlayer.All.SendData(PacketTypes.SignNew, "", i, TSPlayer.Server.Index);
        }
        internal static string ToPlayerPrice((int, int, int, int) r)
        {
            int p = r.Item1, g = r.Item2, s = r.Item3, c = r.Item4;
            string res = "";
            if (p > 0)
                res += p + "p";
            if (g > 0)
                res += g + "g";
            if (s > 0)
                res += s + "s";
            if (c > 0)
                res += c + "c";
            return res;
        }
        internal static void FrameItem(Point point, ShopItem item, bool place = true, bool needSendData = true)
        {
            if (place)
            {
                WorldGen.RangeFrame(point.X, point.Y, point.X + 2, point.Y + 2);
                int num = TEItemFrame.Find(point.X, point.Y);
                if (num == -1)
                    num = TEItemFrame.Place(point.X, point.Y);

                TEItemFrame teitemFrame = (TEItemFrame)TileEntity.ByID[num];
                teitemFrame.item = new Item();
                teitemFrame.item.netDefaults(item.Item.NetId);
                teitemFrame.item.Prefix(0);
                teitemFrame.item.stack = 1; // = 0; Error maybe, need to check
                if (needSendData)
                    NetMessage.SendData(86, -1, -1, null, teitemFrame.ID, (float)point.X, (float)point.Y, 0f, 0, 0, 0);
            }
            else
                TEItemFrame.NetPlaceEntity((int)point.X, (int)point.Y);
        }

        static void Update()
        {
            int left = Math.Min(Region.Area.X, Region.Area.X+Region.Area.Width);
            int right = Math.Max(Region.Area.X, Region.Area.X + Region.Area.Width);

            int top = Math.Min(Region.Area.Y, Region.Area.Y + Region.Area.Height);
            int bottom = Math.Max(Region.Area.Y, Region.Area.Y + Region.Area.Height);

            int sX = Netplay.GetSectionX(left);
            int sX2 = Netplay.GetSectionX(right);
            int sY = Netplay.GetSectionY(top);
            int sY2 = Netplay.GetSectionY(bottom);

            int w = right - left + 1;
            int h = bottom - top + 1;
            bool SendWholeSections = w > 200 || h > 150;

            if (SendWholeSections)
            {
                foreach (RemoteClient sock in from s in Netplay.Clients
                                              where s.IsActive
                                              select s)
                {
                    for (int i = sX; i <= sX2; i++)
                    {
                        for (int j = sY; j <= sY2; j++)
                        {
                            sock.TileSections[i, j] = false;
                        }
                    }
                }
            }
            else
            {
                NetMessage.SendData(10, -1, -1, null, left, (float)top, (float)w, (float)h, 0, 0, 0);
                NetMessage.SendData(11, -1, -1, null, sX, (float)sY, (float)sX2, (float)sY2, 0, 0, 0);
            }
        }
    }

    public class ShopItem
    {
        [JsonIgnore]
        public int ID = -1;

        public int Owner;
        public long Price;

        public NetItem Item;
    }
}