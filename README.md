# ytstreaming

Домашний плеер YouTube Music: сервер получает музыку, а ты слушаешь её в браузере
на телефоне. Есть поиск, обложки, лайки, плейлисты и автоматическое радио.
Аккаунт не нужен. Библиотека хранит ссылки на песни; эфир общий для всех устройств.

## Запуск

Нужны Linux x64/ARM64, Git, Docker с Compose и доступ сервера к YouTube.

```sh
git clone https://github.com/Habrahar/ytstreaming.git
cd ytstreaming
cp .env.example .env
```

В `.env` укажи локальный IP сервера в `BIND_ADDRESS`, затем запусти:

```sh
docker compose pull
docker compose up -d --no-build
```

Готовые образы подходят и для Raspberry Pi с 64-битной ОС; сборка на сервере не нужна.
Открой `http://<IP-сервера>:8090/` на телефоне в той же сети, найди песню и нажми на неё.

Для обновления из папки проекта:

```sh
git pull --ff-only
docker compose pull
docker compose up -d --no-build
```
