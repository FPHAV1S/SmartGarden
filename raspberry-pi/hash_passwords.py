import bcrypt

## Скрипт за хеширане на пароли. Може да се добавят нови пароли също така. Важно е да се отбележи, че bcrypt генерира различен хеш всеки път, дори за една и съща парола, поради използването на salting. Това прави хешовете по-сигурни срещу атаки с wordlist и bruteforcing. Не е нещо много важно за поливна система, но е интересно да се има :D

denis_password = "987654456789"
petur_password = "123456654321"

denis_hash = bcrypt.hashpw(denis_password.encode(), bcrypt.gensalt()).decode()
petur_hash = bcrypt.hashpw(petur_password.encode(), bcrypt.gensalt()).decode()

print(f"Denis hash: {denis_hash}")
print(f"Petur hash: {petur_hash}")
