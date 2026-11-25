# Join this repository's store

Part of `/mcred:init`, its `## 4`, for `format=2` or `format=3` with `seat=none`. `<repo>` and `<mcred>` are as `## 1 Look` of `SKILL.md`
found them.

This applies to a new developer. It also applies to a person who already has a password when that password's root key
has no slot in this store: they may have run `mcred init` elsewhere, or they type the same password at the prompt below.

A join takes two people. A **holder** is a seat that already has a slot. The session never runs `mcred seat invite` or
`mcred seat join`, and never sees the one-off root key.

## 1. Who holds a slot

From inside `<repo>`, `<mcred> seat list` prints `NAME`, `KIND`, `ADDED`, `BY` and `EXPIRES`. The rows of kind `seat` are
the holders. Show them to the person, because one of them has to invite.

If a row of kind `one-off` looks like the person's and its `EXPIRES` is still ahead, go to `## 4`.

## 2. The seat's name

Ask (AskUserQuestion): the OS user's name, or another. It must match `^[a-z0-9][a-z0-9-]{0,62}$`, and no row of `seat list`
may have it already.

## 3. The message for the holder

Draft this for the person to send, with the repository and the name filled in:

> Please add me to the mcred store of `<repository>`. In that repository, in your own terminal and not in a Claude Code
> session, run `mcred seat invite <name>`. Then commit and push `.mcred-store/repository.json`, and send me the one-off
> root key it prints, in private: face to face, a call, or an end-to-end encrypted chat. Never email, the repository or a
> ticket. It expires in 72 hours.

The person says when the holder has pushed.

## 4. Check the invite

`git -C "<repo>" pull`, on the person's word. Then `<mcred> seat list` must show a row `<name>` of kind `one-off`, with its
`EXPIRES` still ahead.

- No such row: the push has not arrived, or the name differs.
- Expired: the holder removes it (`mcred seat remove <name>`) and invites again, by hand.

## 5. The person joins

The person runs this in their own terminal, from inside `<repo>` (on Windows a PowerShell console, never mintty and never
`!` in this session):

```
<mcred> seat join <name>
```

- If the seat has no root key, it asks for the password twice, without echo. A person with a password from another seat
  types that one, and it cannot be checked here, so a typing mistake makes a different root key. A new developer chooses
  one of 12 characters or more, best a passphrase of six random words. It is the seat's backup.
- Then it asks for the one-off root key.
- The root key is saved only when the join succeeds, so after a failure the person can simply run it again.

If it says that the password or root key already has a slot here, the person has a seat in this store already. With no
root key on this seat, they run `mcred init` with that password here. A holder then removes the unused one-off slot.

## 6. Afterwards

- `<mcred> status` must show `seat=<name>`.
- Offer to commit `.mcred-store/repository.json` (message: `mcred: seat <name> joined`), and push on the person's word. The
  one-off slot is gone from the file once this is pushed.
