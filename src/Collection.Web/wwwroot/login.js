'use strict';
document.querySelector('#login-form').onsubmit=async e=>{
 e.preventDefault();const button=e.currentTarget.querySelector('button');button.disabled=true;
 try {
  const r=await fetch('/api/login',{method:'POST',headers:{'Content-Type':'application/json','X-Collection-Client':'local-ui'},body:JSON.stringify({key:document.querySelector('#access-key').value})});
  if(!r.ok)throw Error((await r.json()).error||'连接失败');
  location.replace('/');
 } catch(error){document.querySelector('#login-error').textContent=error.message;}
 finally{button.disabled=false;}
};
