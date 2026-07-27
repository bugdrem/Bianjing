using System;

namespace Bianjing;

/// <summary>宋代风格随机姓名。子女随父姓。</summary>
public static class NameGenerator
{
    private static readonly Random Rng = new();

    private static readonly string[] Surnames =
    {
        "赵", "钱", "孙", "李", "周", "吴", "郑", "王",
        "冯", "陈", "褚", "卫", "蒋", "沈", "韩", "杨",
        "朱", "秦", "尤", "许", "何", "吕", "施", "张",
    };

    private const string MaleChars = "德明志刚勇文山河海松柏福贵安康仁义礼智信忠孝";
    private const string FemaleChars = "秀兰香玉英珍芳梅桂凤云霞月娥巧慧淑贞静婉";

    public static string RandomSurname() => Surnames[Rng.Next(Surnames.Length)];

    public static string GivenName(Gender gender)
    {
        string pool = gender == Gender.Male ? MaleChars : FemaleChars;
        int len = Rng.Next(2) + 1;
        string given = "";
        for (int i = 0; i < len; i++)
            given += pool[Rng.Next(pool.Length)];
        return given;
    }

    public static (string surname, string fullName) NewName(Gender gender, string surname = null)
    {
        surname ??= RandomSurname();
        return (surname, surname + GivenName(gender));
    }
}
