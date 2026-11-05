import re,sys,io
def dump(p):
    b=open(p,'rb').read()
    out=set()
    for m in re.finditer(rb'[\x20-\x7e\t]{4,}',b):
        out.add(m.group(0).decode('latin1'))
    # utf16le
    for m in re.finditer(rb'(?:[\x20-\x7e]\x00){4,}',b):
        out.add(m.group(0).decode('utf-16le'))
    return out
if __name__=='__main__':
    p=sys.argv[1]
    for s in sorted(dump(p)):
        print(s)
