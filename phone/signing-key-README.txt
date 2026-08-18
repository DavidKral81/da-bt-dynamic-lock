PODPISOVÝ KLÍČ K APLIKACI PRO ANDROID — NEMAZAT
================================================

Soubor:  signing-key-DO-NOT-DELETE.jks

K čemu je
---------
Každá aplikace pro Android musí být podepsaná. Android pak dovolí
aktualizaci jen tehdy, když je nová verze podepsaná TÍMŽ klíčem.

Když se tenhle soubor ztratí
----------------------------
Aplikaci v telefonu už nepůjde aktualizovat. Bude se muset nejdřív
odinstalovat a nainstalovat znovu — tím se ztratí i její nastavení
a udělená oprávnění. Nic vážnějšího se nestane, ale je to otrava.

Proč leží tady, a ne u nástrojů
-------------------------------
Sestavovací nástroje jsou ve složce `_android-build` (~2,5 GB) a ta je
určená k smazání, až nebude potřeba. Klíč tam proto nepatří — patří
k projektu.

Heslo
-----
Heslo NENÍ v tomto souboru ani v sestavovacím skriptu —
leží vedle klíče v  signing-key-password.txt , který je v .gitignore
a nikdy se nedostane do repozitáře. Sestavovací skript si ho odtud
přečte sám; místo souboru lze použít i proměnnou prostředí
DDL_KEYSTORE_PASSWORD.

Bez toho souboru NELZE aplikaci v telefonu aktualizovat — zálohuj
ho spolu s klíčem.

Výměna klíče 18.08.2026
-----------------------
Klíč byl vyměněn za nový, protože ten původní nesl staré jméno
`CN=Da Dynamic Lock` (z doby před přejmenováním projektu).

  nový   CN=Da BT Dynamic Lock, O=David, C=CZ
         otisk SHA-256  6618ce94260f1b3a0944d4d59f594ae27729bbf00256edcddb5c3ad36bb1c7ae
  starý  CN=Da Dynamic Lock  — zachovaný v
         signing-key-OLD-CN-Da-Dynamic-Lock.jks

DŮSLEDEK: aplikaci podepsanou novým klíčem NELZE nainstalovat přes
starou. V telefonu se musí ta stará nejdřív odinstalovat.

Starý klíč se nemaže: kdyby se někdy ukázalo, že je potřeba vydat
aktualizaci pro telefon, kde ještě běží stará verze, jde s ním podepsat.

Zálohování
----------
Klíč patří do zálohy spolu se souborem s heslem. Když se projekt stěhuje,
musí jít oba soubory s ním — v repozitáři nejsou.
